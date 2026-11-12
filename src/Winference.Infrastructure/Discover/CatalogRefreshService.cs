using System.Security.Cryptography;
using System.Text.Json;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Discover;

/// <summary>The fingerprints of the lists this app last fetched and checked itself.</summary>
public sealed record CatalogRefreshRecord(int SchemaVersion, DateTimeOffset RefreshedUtc, Dictionary<string, string> Files);

public sealed record CatalogRefreshResult(bool Succeeded, int UpdatedFiles, string? Problem);

/// <summary>
/// Keeps the model lists current between engine updates. Lists come only from the engine makers'
/// fixed upstream addresses. Each is size-limited and checked before it replaces the saved copy,
/// and the app's bundled copy is used whenever no checked copy is present.
/// </summary>
public sealed class CatalogRefreshService
{
    public const string FlmList = "flm-model_list.json";
    public const string FlmInfo = "flm-model_info.json";
    public const string LemonadeModels = "lemonade-server_models.json";
    public const string RecordFile = "refresh.json";
    private const int MaximumBytes = 4 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, Uri> Upstream = new Dictionary<string, Uri>
    {
        [FlmList] = new("https://raw.githubusercontent.com/ROCm/FastFlowLM/main/src/model_list.json"),
        [FlmInfo] = new("https://raw.githubusercontent.com/ROCm/FastFlowLM/main/src/model_info.json"),
        [LemonadeModels] = new("https://raw.githubusercontent.com/lemonade-sdk/lemonade/main/src/cpp/resources/server_models.json"),
    };

    private readonly string _applicationDirectory;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CatalogRefreshService(string dataRoot, string? applicationDirectory = null, HttpClient? http = null)
    {
        Directory = Path.Combine(Path.GetFullPath(dataRoot), "Catalogs");
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        _http = http ?? new HttpClient(new HttpClientHandler { MaxAutomaticRedirections = 3 }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Directory { get; }

    public CatalogRefreshRecord? Record => ReadRecord(Directory);

    public DateTimeOffset? RefreshedUtc => Record?.RefreshedUtc;

    public bool IsStale(TimeSpan maximumAge, DateTimeOffset? now = null) =>
        RefreshedUtc is not { } refreshed || (now ?? DateTimeOffset.UtcNow) - refreshed > maximumAge;

    /// <summary>The checked, refreshed copy of a list when there is one; otherwise the copy bundled with the app.</summary>
    public string PathFor(string name)
    {
        var refreshed = Path.Combine(Directory, name);
        return IsTrusted(refreshed) ? refreshed : BundledPath(name);
    }

    public string BundledPath(string name) => name switch
    {
        FlmList => Path.Combine(_applicationDirectory, "Catalogs", "catalog.json"),
        FlmInfo => Path.Combine(_applicationDirectory, "Catalogs", "files.json"),
        LemonadeModels => Path.Combine(_applicationDirectory, "Runtimes", "Lemonade", "resources", "server_models.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    /// <summary>The FastFlowLM pair to use. Both files must come from the same refresh, or both are the bundled copies.</summary>
    public static (string Catalog, string Info) ResolveFlm(string dataRoot, string? applicationDirectory = null)
    {
        var service = new CatalogRefreshService(dataRoot, applicationDirectory, NoNetwork.Instance);
        var list = Path.Combine(service.Directory, FlmList);
        var info = Path.Combine(service.Directory, FlmInfo);
        return IsTrusted(list) && IsTrusted(info) ? (list, info) : (service.BundledPath(FlmList), service.BundledPath(FlmInfo));
    }

    /// <summary>True when this exact file was fetched, checked and recorded by a refresh.</summary>
    public static bool IsTrusted(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var record = ReadRecord(Path.GetDirectoryName(Path.GetFullPath(path))!);
            return record?.Files.TryGetValue(Path.GetFileName(path), out var expected) == true &&
                expected == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fetched = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var (name, source) in Upstream)
            {
                try { fetched[name] = await FetchAsync(source, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
                { problems.Add(name); }
            }
            // The FastFlowLM list and its file details must match each other, so they move together.
            if (!fetched.ContainsKey(FlmList) || !fetched.ContainsKey(FlmInfo) ||
                !IsFlmList(fetched[FlmList]) || !IsJsonObject(fetched[FlmInfo]))
            { fetched.Remove(FlmList); fetched.Remove(FlmInfo); }
            if (fetched.TryGetValue(LemonadeModels, out var lemonade) && !IsLemonadeRegistry(lemonade)) fetched.Remove(LemonadeModels);
            if (fetched.Count == 0)
                return new(false, 0, "The model lists could not be updated. Check your internet connection and try again.");

            System.IO.Directory.CreateDirectory(Directory);
            var files = new Dictionary<string, string>(Record?.Files ?? [], StringComparer.Ordinal);
            foreach (var (name, bytes) in fetched)
            {
                var target = Path.Combine(Directory, name);
                var staging = target + ".new";
                await File.WriteAllBytesAsync(staging, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(staging, target, overwrite: true);
                files[name] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
            await AtomicJson.WriteAsync(Path.Combine(Directory, RecordFile),
                new CatalogRefreshRecord(1, DateTimeOffset.UtcNow, files), cancellationToken).ConfigureAwait(false);
            var missed = Upstream.Count - fetched.Count;
            return new(true, fetched.Count, missed == 0 ? null : "Some model lists couldn't be updated, so their previous copies are still used.");
        }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> FetchAsync(Uri source, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumBytes) throw new InvalidDataException("The model list is unexpectedly large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("The model list is unexpectedly large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static bool IsFlmList(byte[] bytes) => TryParse(bytes, root =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var models) &&
        models.ValueKind == JsonValueKind.Object && models.EnumerateObject().Any());

    internal static bool IsJsonObject(byte[] bytes) => TryParse(bytes, root => root.ValueKind == JsonValueKind.Object);

    internal static bool IsLemonadeRegistry(byte[] bytes) => TryParse(bytes, root =>
        root.ValueKind == JsonValueKind.Object &&
        root.EnumerateObject().Any(entry => entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("recipe", out _)));

    private static bool TryParse(byte[] bytes, Func<JsonElement, bool> check)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            return check(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static CatalogRefreshRecord? ReadRecord(string directory)
    {
        try
        {
            var path = Path.Combine(directory, RecordFile);
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<CatalogRefreshRecord>(File.ReadAllBytes(path), AtomicJson.Options);
            return record is { SchemaVersion: 1, Files: not null } ? record : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Path lookups never touch the network.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        public static readonly HttpClient Instance = new(new NoNetwork());
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Path lookups must not use the network.");
    }
}
