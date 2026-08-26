using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winference.Core.Runtime;
using Winference.Core.Setup;

namespace Winference.Infrastructure.Setup;

public sealed record SupplierFile(string Name, long LengthBytes, string? Sha256, string? GitBlobSha1);

public sealed record CatalogModel(string Id, string DirectoryName, string DisplayName, string Family,
    string Publisher, string Description, int ContextLength, string MinimumRuntimeVersion,
    IReadOnlyList<SupplierFile> Files, string? UnavailableReason, bool SupportsImages = false)
{
    public long? StorageBytes => Files.Count > 0 && Files.All(f => f.LengthBytes > 0) ? Files.Sum(f => f.LengthBytes) : null;
    public string ProviderId => Id.Replace(':', '-') + "-FLM";
}

public sealed class FlmModelCatalog
{
    public const string Revision = "FastFlowLM/v1.0.10";
    public IReadOnlyList<CatalogModel> Models { get; }

    private FlmModelCatalog(IReadOnlyList<CatalogModel> models) => Models = models;

    public static FlmModelCatalog Load(string catalogPath, string modelInfoPath)
    {
        var catalogBytes = File.ReadAllBytes(catalogPath);
        var metadataBytes = File.ReadAllBytes(modelInfoPath);
        if (Convert.ToHexStringLower(SHA256.HashData(catalogBytes)) != "6849fc3c7776b884c308b5373de5aa5230c648a2eb3051c4f9367341076b9090" ||
            Convert.ToHexStringLower(SHA256.HashData(metadataBytes)) != "c116977e0c00a7e9b1f76c47ee93a152e25a2055bd1e7229544f46fb70b84e53")
            throw new RuntimeException("catalog_integrity", "The model catalog differs from this build's verified catalog. Restore the complete app folder.");
        using var catalog = JsonDocument.Parse(catalogBytes);
        using var metadata = JsonDocument.Parse(metadataBytes);
        var models = new List<CatalogModel>();
        foreach (var family in catalog.RootElement.GetProperty("models").EnumerateObject())
        foreach (var variant in family.Value.EnumerateObject())
        {
            var entry = variant.Value;
            var labels = Strings(entry, "label");
            // Vision models can chat and also see pictures. Audio-input models aren't supported yet.
            if (Bool(entry, "asr") || labels.Any(x => x is "single-turn" or "embeddings" or "transcription" or "chat-transcription") ||
                !Strings(entry, "supported_platforms").Contains("aie2p")) continue;
            var id = family.Name + ":" + variant.Name;
            var directory = String(entry, "name");
            if (string.IsNullOrWhiteSpace(directory) || directory.IndexOfAny(['/', '\\', ':']) >= 0 || directory is "." or "..") continue;
            var infoKey = String(entry, "model_info_key") ?? id;
            var files = new List<SupplierFile>();
            var completeManifest = metadata.RootElement.TryGetProperty(infoKey, out var supplier) && supplier.ValueKind == JsonValueKind.Array;
            foreach (var filename in Strings(entry, "files"))
            {
                if (!IsSafeRelativeFile(filename)) { completeManifest = false; break; }
                var record = completeManifest ? supplier.EnumerateArray().FirstOrDefault(f => String(f, "path") == filename) : default;
                if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length <= 0)
                { completeManifest = false; break; }
                var sha256 = record.TryGetProperty("lfs", out var lfs) ? String(lfs, "oid") : String(record, "sha256");
                var git = sha256 is null ? String(record, "oid") : null;
                if ((sha256 is not null && !IsHash(sha256, 64)) || (sha256 is null && (git is null || !IsHash(git, 40))))
                { completeManifest = false; break; }
                files.Add(new(filename, length, sha256, git));
            }
            var minimum = String(entry, "flm_min_version") ?? "1.0.10";
            var reason = !completeManifest || files.Count == 0 ? "The catalog lacks a complete verified file list for this model." :
                !Version.TryParse(minimum.TrimStart('v'), out var required) || required > new Version(1, 0, 10)
                    ? "This model needs a newer AI component than this build supports." : null;
            var details = entry.TryGetProperty("details", out var foundDetails) ? foundDetails : default;
            var description = string.Join(" · ", new[] { String(details, "parameter_size"), String(details, "quantization_level"), Bool(entry, "vlm") ? "Sees images" : labels.Contains("reasoning") ? "Reasoning model" : "Text chat" }.Where(s => !string.IsNullOrEmpty(s)));
            var context = entry.TryGetProperty("default_context_length", out var ctx) && ctx.TryGetInt32(out var value) && value > 256 ? value : 2048;
            models.Add(new(id, directory, directory.Replace("-NPU2", "", StringComparison.Ordinal),
                String(details, "family") ?? family.Name, "FastFlowLM catalog", description, context, minimum, files, reason, Bool(entry, "vlm")));
        }
        return new(models.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
