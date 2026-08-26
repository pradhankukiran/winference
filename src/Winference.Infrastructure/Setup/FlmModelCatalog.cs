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

    public IReadOnlyList<SetupModel> Discover(IReadOnlyList<string> roots, bool npuDetected, long memoryBytes = 0)
    {
        return Models.Select(model =>
        {
            var complete = roots.FirstOrDefault(root => HasCompleteFiles(root, model));
            var incomplete = complete is null && roots.Any(root => Directory.Exists(Path.Combine(root, "models", model.DirectoryName)));
            return new SetupModel(model.Id, model.DisplayName, model.Publisher, model.Description,
                complete is null ? model.StorageBytes : 0, model.StorageBytes,
                complete is not null ? "Installed" : incomplete ? "Incomplete" : "Not installed",
                complete, model.UnavailableReason ?? (!npuDetected ? "A supported AMD NPU is required by this build." :
                    memoryBytes > 0 && model.StorageBytes is { } size && size > memoryBytes ? "This model's files exceed this computer's installed memory. Choose a smaller model." : null),
                model.ContextLength, Math.Min(model.ContextLength, 4096), Revision);
        }).OrderByDescending(m => m.IsInstalled).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool HasCompleteFiles(string root, CatalogModel model)
    {
        try
        {
            return model.Files.Count > 0 && model.Files.All(file =>
            {
                var path = Path.Combine(root, "models", model.DirectoryName, file.Name);
                return File.Exists(path) && new FileInfo(path).Length == file.LengthBytes;
            });
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static async Task<IReadOnlyList<VerifiedModelFile>> VerifyFilesAsync(string root, CatalogModel model,
        IProgress<SetupProgress>? progress, CancellationToken token)
    {
        var result = new List<VerifiedModelFile>();
        foreach (var file in model.Files)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(root, "models", model.DirectoryName, file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.LengthBytes)
                throw new RuntimeException("model_incomplete", "The selected model is incomplete. Existing files were left alone; choose an app-owned download location to retry.");
            progress?.Report(new("CheckingFiles", $"Verifying {file.Name}…"));
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var git = file.GitBlobSha1 is not null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
            git?.AppendData(Encoding.UTF8.GetBytes($"blob {input.Length}\0"));
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { sha.AppendData(buffer, 0, read); git?.AppendData(buffer, 0, read); }
            var digest = Convert.ToHexStringLower(sha.GetHashAndReset());
            if ((file.Sha256 is not null && digest != file.Sha256) ||
                (file.GitBlobSha1 is not null && Convert.ToHexStringLower(git!.GetHashAndReset()) != file.GitBlobSha1))
                throw new RuntimeException("model_integrity", "The selected model differs from its supplier checksum. It has not been loaded or modified.");
            result.Add(new(file.Name, file.LengthBytes, digest));
        }
        return result;
    }

    private static bool IsSafeRelativeFile(string file) => !Path.IsPathRooted(file) && !file.Contains('\\') && !file.Contains(':') &&
        file.Split('/').All(part => part.Length > 0 && part is not "." and not "..");
    private static bool IsHash(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    private static string? String(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string[] Strings(JsonElement element, string key) => element.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
}

public sealed record VerifiedModelFile(string Name, long LengthBytes, string Sha256);
