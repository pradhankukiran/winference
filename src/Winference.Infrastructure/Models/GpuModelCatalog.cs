using System.Security.Cryptography;
using System.Text.Json;
using Winference.Core.Models;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Models;

/// <summary>One approved GPU artifact. The URL, byte length and digest are pinned together.</summary>
public sealed record GpuCatalogModel(
    string Id,
    ModelFamily Family,
    string FileName,
    Uri Source,
    Uri SourcePage,
    string Revision,
    string Sha256,
    long LengthBytes,
    string Quantization,
    int ContextLength)
{
    /// <summary>Mirrors the GPU registration's bounded profile context so identity does not change after download.</summary>
    public const int MaximumProfileContextLength = 2048;

    public string ArtifactId => "sha256:" + Sha256;
    public int ProfileContextLength => Math.Min(MaximumProfileContextLength, ContextLength) / 256 * 256;
    public string ProfileId => $"gguf-{Sha256}-vulkan-c{ProfileContextLength}";
    public string DisplayName => Family.DisplayName;

    public ModelArtifact Artifact => new(
        ArtifactId, Family, ModelArtifactFormat.Gguf, Quantization,
        SourcePage.ToString(), Revision, LengthBytes, Sha256, "Vulkan llama.cpp");

    public void Validate()
    {
        if (Id.Length is < 3 or > 80 || Id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')))
            throw new ArgumentException("A GPU catalog ID must use ASCII letters, digits, dots, dashes or underscores.", nameof(Id));
        ArgumentNullException.ThrowIfNull(Family);
        Family.Validate();
        if (!IsSafeFileName(FileName) || !FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A GPU catalog file must be one safe .gguf file name.", nameof(FileName));
        if (Source.Scheme != Uri.UriSchemeHttps || SourcePage.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Approved GPU downloads and source pages must use HTTPS.", nameof(Source));
        if (Revision.Length != 40 || !Revision.All(Uri.IsHexDigit))
            throw new ArgumentException("A GPU catalog revision must be a pinned 40-character commit ID.", nameof(Revision));
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit) || Sha256 != Sha256.ToLowerInvariant())
            throw new ArgumentException("A GPU catalog SHA-256 must contain 64 lowercase hexadecimal characters.", nameof(Sha256));
        if (LengthBytes is <= 0 or > 200L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(LengthBytes));
        if (Quantization.Length is 0 or > 40) throw new ArgumentOutOfRangeException(nameof(Quantization));
        if (ContextLength is < 512 or > 2_000_000) throw new ArgumentOutOfRangeException(nameof(ContextLength));
    }

    internal static bool IsSafeFileName(string name) => name.Length is >= 6 and <= 240 && name[0] != '.' && name == name.Trim() &&
        name.All(character => character is >= ' ' and <= '~' && "\\/:*?\"<>|".Contains(character) == false);
}

/// <summary>The build's approved, versioned GGUF mappings. Loading verifies the whole catalog digest.</summary>
public sealed class GpuModelCatalog
{
    public const string Revision = "winference-approved-gguf/1";
    public const string PinnedSha256 = "b1ac5a5bd557f4f694fa9041532338275e9e08ad08f4e3029308a6eb8aab9184";
    private const int MaximumCatalogBytes = 1024 * 1024;
    private const int MaximumModels = 64;

    public IReadOnlyList<GpuCatalogModel> Models { get; }
    public string Attribution { get; }

    private GpuModelCatalog(IReadOnlyList<GpuCatalogModel> models, string attribution)
    {
        Models = models;
        Attribution = attribution;
    }

    public static GpuModelCatalog Load(string path, string? expectedSha256 = null)
    {
        if (expectedSha256 is not null && (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("The expected catalog digest is invalid.", nameof(expectedSha256));
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new RuntimeException("gpu_catalog_state", "The approved GPU model catalog could not be read. Restore the complete Winference application folder.", error); }
        if (bytes.Length is 0 or > MaximumCatalogBytes)
            throw new RuntimeException("gpu_catalog_integrity", "The approved GPU model catalog has an unexpected size. Restore the complete Winference application folder.");
        if (expectedSha256 is not null && Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedSha256.ToLowerInvariant())
            throw new RuntimeException("gpu_catalog_integrity", "The approved GPU model catalog differs from this build's verified catalog. Restore the complete Winference application folder.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "schemaVersion") != 1 ||
                Text(root, "revision") != Revision)
                throw Invalid();
            var attribution = Text(root, "attribution");
            if (string.IsNullOrWhiteSpace(attribution)) throw Invalid();
            if (!root.TryGetProperty("models", out var modelsElement) || modelsElement.ValueKind != JsonValueKind.Array)
                throw Invalid();
            var models = new List<GpuCatalogModel>();
            foreach (var entry in modelsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw Invalid();
                var id = RequiredText(entry, "id");
                var familyId = RequiredText(entry, "familyId");
                var family = new ModelFamily(familyId, RequiredText(entry, "displayName"),
                    RequiredText(entry, "publisher"), RequiredText(entry, "description"));
                family.Validate();
                var model = new GpuCatalogModel(
                    id, family, RequiredText(entry, "fileName"),
                    ParseUri(RequiredText(entry, "url")), ParseUri(RequiredText(entry, "sourcePage")),
                    RequiredText(entry, "revision"), RequiredText(entry, "sha256"),
                    Long(entry, "lengthBytes"), RequiredText(entry, "quantization"), Int(entry, "contextLength"));
                model.Validate();
                models.Add(model);
            }
            if (models.Count is 0 or > MaximumModels ||
                models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != models.Count ||
                models.Select(model => model.Sha256).Distinct(StringComparer.Ordinal).Count() != models.Count)
                throw Invalid();
            return new(models.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(), attribution);
        }
        catch (JsonException error) { throw Invalid(error); }
        catch (Exception error) when (error is ArgumentException or UriFormatException or InvalidOperationException or KeyNotFoundException)
        { throw Invalid(error); }
    }

    private static RuntimeException Invalid(Exception? error = null) =>
        new("gpu_catalog_integrity", "The approved GPU model catalog is not valid. Restore the complete Winference application folder.", error);
    private static string RequiredText(JsonElement element, string key) =>
        Text(element, key) is { Length: > 0 } value ? value : throw Invalid();
    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int Int(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) ? number : throw Invalid();
    private static long Long(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.TryGetInt64(out var number) ? number : throw Invalid();
    private static Uri ParseUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : throw Invalid();
}
