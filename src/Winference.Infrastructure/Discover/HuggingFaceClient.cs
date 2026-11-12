using System.Text.Json;
using System.Text.RegularExpressions;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Discover;

/// <summary>One GGUF file pinned to a repository commit, with the fingerprint Hugging Face publishes for it.</summary>
public sealed record HuggingFaceFile(string Repository, string Revision, string FileName, string Sha256, long LengthBytes)
{
    public Uri Download => new($"https://huggingface.co/{Repository}/resolve/{Revision}/{Uri.EscapeDataString(FileName)}");
    public Uri SourcePage => new($"https://huggingface.co/{Repository}");
}

/// <summary>One file in a repository: large files carry a SHA-256, small ones a git blob SHA-1.</summary>
public sealed record HuggingFaceRepositoryFile(string Name, long LengthBytes, string? Sha256, string? GitSha1);

/// <summary>A Hugging Face repository found by search. Uploads there are community work, not tested by Winference.</summary>
public sealed record HuggingFaceRepository(string Id, long Downloads, long Likes)
{
    public string Owner => Id[..Id.IndexOf('/')];
    public string Name => Id[(Id.IndexOf('/') + 1)..];
}

/// <summary>
/// Read-only lookups on Hugging Face's public model API. Nothing is downloaded here; a lookup only
/// pins the exact commit, file name, size and SHA-256 that a later download must match.
/// </summary>
public sealed partial class HuggingFaceClient(HttpClient? http = null)
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Public GGUF repositories matching the words, most downloaded first.</summary>
    public async Task<IReadOnlyList<HuggingFaceRepository>> SearchGgufAsync(string query, CancellationToken cancellationToken = default)
    {
        var words = (query ?? string.Empty).Trim();
        if (words.Length is < 2 or > 80) return [];
        using var document = await GetJsonAsync(new Uri("https://huggingface.co/api/models?filter=gguf&sort=downloads&direction=-1&limit=24&search="
            + Uri.EscapeDataString(words)), cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        var repositories = new List<HuggingFaceRepository>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("private", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            if (!item.TryGetProperty("id", out var id) || id.GetString() is not { } name || !LemonadeRegistry.IsRepository(name)) continue;
            repositories.Add(new(name, Number(item, "downloads"), Number(item, "likes")));
        }
        return repositories;
    }

    /// <summary>Every plain, single-file GGUF in a repository, each pinned to the current commit and fingerprint.</summary>
    public async Task<IReadOnlyList<HuggingFaceFile>> ListGgufFilesAsync(string repository, CancellationToken cancellationToken = default)
    {
        var (revision, files) = await ReadRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
        return files.Where(file => IsSingleModelFile(file.Name) && file.Sha256 is not null && file.Length > 0)
            .Select(file => new HuggingFaceFile(repository, revision, file.Name, file.Sha256!, file.Length))
            .OrderBy(file => file.LengthBytes).ToArray();
    }

    /// <summary>Every file in a repository at its current commit, with the fingerprint each can be verified against.</summary>
    public async Task<(string Revision, IReadOnlyList<HuggingFaceRepositoryFile> Files)> ListFilesAsync(string repository,
        CancellationToken cancellationToken = default)
    {
        var (revision, files) = await ReadRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
        return (revision, files.Select(file => new HuggingFaceRepositoryFile(file.Name, file.Length, file.Sha256, file.GitSha1)).ToArray());
    }

    /// <summary>Finds the GGUF file a registry entry names, either exactly or by its quantization.</summary>
    public async Task<HuggingFaceFile> ResolveGgufAsync(string repository, string? fileName, string? quantization,
        CancellationToken cancellationToken = default)
    {
        var (revision, files) = await ReadRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
        var chosen = fileName is not null
            ? files.FirstOrDefault(file => file.Name == fileName)
            : files.Where(file => IsSingleModelFile(file.Name) && MatchesQuantization(file.Name, quantization ?? string.Empty))
                .OrderBy(file => file.Name.Length).FirstOrDefault();
        if (chosen is null) throw Problem("The model file listed for this model wasn't found on Hugging Face. Refresh the model list and try again.");
        if (chosen.Sha256 is null || chosen.Length <= 0)
            throw Problem("Hugging Face didn't publish a fingerprint for this file, so it can't be verified. It wasn't downloaded.");
        return new(repository, revision, chosen.Name, chosen.Sha256, chosen.Length);
    }

    private async Task<(string Revision, FileEntry[] Files)> ReadRepositoryAsync(string repository, CancellationToken cancellationToken)
    {
        if (!LemonadeRegistry.IsRepository(repository)) throw Problem("This model's source address isn't valid.");
        using var document = await GetJsonAsync(new Uri($"https://huggingface.co/api/models/{repository}?blobs=true"), cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("gated", out var gated) && gated.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            throw Problem("This model needs a Hugging Face account and accepted terms, so Winference can't download it.");
        var revision = root.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
        if (revision is null || !IsHex(revision, 40)) throw Problem("Hugging Face didn't report a version for this model. Try again later.");

        var files = root.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array
            ? siblings.EnumerateArray().Select(ReadFile).Where(file => file is not null).Select(file => file!).ToArray() : [];
        return (revision, files);
    }

    private static long Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    /// <summary>A plain, single-file GGUF: no vision projector and no split parts.</summary>
    public static bool IsSingleModelFile(string name) =>
        name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !name.Contains('/') &&
        !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase) && !SplitPart().IsMatch(name);

    public static bool MatchesQuantization(string name, string quantization)
    {
        if (quantization.Length == 0) return false;
        var stem = name[..^".gguf".Length];
        return stem.EndsWith(quantization, StringComparison.OrdinalIgnoreCase) &&
            stem.Length > quantization.Length && stem[^(quantization.Length + 1)] is '-' or '.' or '_';
    }

    private async Task<JsonDocument> GetJsonAsync(Uri source, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized)
            throw Problem("This model is no longer available on Hugging Face.");
        if (!response.IsSuccessStatusCode) throw Problem("Hugging Face couldn't be reached. Check your internet connection and try again.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes) throw Problem("Hugging Face sent an unexpectedly large reply.");
            buffer.Write(chunk, 0, read);
        }
        try { return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw Problem("Hugging Face sent a reply Winference couldn't read. Try again later."); }
    }

    private static FileEntry? ReadFile(JsonElement sibling)
    {
        if (!sibling.TryGetProperty("rfilename", out var name) || name.GetString() is not { Length: > 0 } fileName) return null;
        string? sha256 = null;
        long length = sibling.TryGetProperty("size", out var plainSize) && plainSize.TryGetInt64(out var bytes) ? bytes : 0;
        if (sibling.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
        {
            sha256 = lfs.TryGetProperty("sha256", out var hash) ? hash.GetString() : null;
            if (lfs.TryGetProperty("size", out var size)) size.TryGetInt64(out length);
        }
        var blob = sibling.TryGetProperty("blobId", out var blobId) ? blobId.GetString() : null;
        return new(fileName, sha256 is not null && IsHex(sha256, 64) ? sha256.ToLowerInvariant() : null, length,
            sha256 is null && blob is not null && IsHex(blob, 40) ? blob.ToLowerInvariant() : null);
    }

    private static bool IsHex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    private static RuntimeException Problem(string message) => new("huggingface", message);

    private sealed record FileEntry(string Name, string? Sha256, long Length, string? GitSha1 = null);

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPart();
}
