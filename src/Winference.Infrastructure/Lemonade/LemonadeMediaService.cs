using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winference.Core.Media;
using Winference.Core.Runtime;
using Winference.Infrastructure.Setup;
using Winference.Infrastructure.Windows;

namespace Winference.Infrastructure.Lemonade;

/// <summary>
/// A second, app-owned Lemonade server for speech input, natural voices and images.
/// Unlike the NPU chat server it may download: models from Hugging Face and engines from
/// Lemonade's pinned, checksum-verified GitHub releases. It listens on loopback with a
/// random key, starts on first use and stops after a few idle minutes.
/// </summary>
public sealed class LemonadeMediaService : IAsyncDisposable
{
    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(5);
    private const string Version = "2026.42.1";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _lemonadePath;
    private readonly string _root;
    private readonly string _installedPath;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _sync = new();
    private OwnedProcessSession? _owned;
    private HttpClient? _http;
    private int _busy;
    private CancellationTokenSource? _idle;

    public LemonadeMediaService(string lemonadePath, string dataRoot)
    {
        _lemonadePath = Path.GetFullPath(lemonadePath);
        _root = Path.Combine(Path.GetFullPath(dataRoot), "Media");
        _installedPath = Path.Combine(_root, "installed.json");
    }

    public event EventHandler? InstalledChanged;

    /// <summary>Models this app finished downloading and setting up. Read without starting the server.</summary>
    public IReadOnlySet<string> Installed
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return File.Exists(_installedPath)
                        ? (JsonSerializer.Deserialize<string[]>(File.ReadAllText(_installedPath)) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    public bool IsInstalled(string model) => Installed.Contains(model);

    /// <summary>Downloads a model, then loads it once so Lemonade fetches and verifies its engine.</summary>
    public async Task InstallAsync(string model, IProgress<MediaDownloadProgress> progress, IProgress<string> stage, CancellationToken token)
    {
        RequireKnown(model);
        await UseAsync(async http =>
        {
            stage.Report("Downloading");
            using var request = new HttpRequestMessage(HttpMethod.Post, "pull") { Content = JsonContent.Create(new { model_name = model, stream = true }) };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var parser = new PullStreamParser();
            var completed = false;
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (parser.Feed(line) is not { } update) continue;
                progress.Report(update);
                if (update.Complete) { completed = true; break; }
            }
            if (!completed) throw new RuntimeException("media_download", "The download stopped before it finished. Check your internet connection and try again.");

            // The first load installs the engine. Unload afterwards so memory is free until it's used.
            stage.Report("Setting up");
            await PostAsync(http, "load", new { model_name = model }, token, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            await PostAsync(http, "unload", new { model_name = model }, token, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
        SetInstalled(model, true);
    }

    public async Task RemoveAsync(string model, CancellationToken token)
    {
        RequireKnown(model);
        await UseAsync(async http =>
        {
            try { await PostAsync(http, "unload", new { model_name = model }, token, TimeSpan.FromMinutes(1)).ConfigureAwait(false); }
            catch (RuntimeException) { }
            await PostAsync(http, "delete", new { model_name = model }, token, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
        SetInstalled(model, false);
        // The engine's files are locked while the server runs, so stop it before cleaning up.
        await StopAsync().ConfigureAwait(false);
        try
        {
            MediaAddons.CleanUp(MediaAddons.Find(model)!, Installed, Path.Combine(_root, "models"), Path.Combine(_root, "cache", "bin"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeException("media_cleanup", "The add-on was removed, but some of its files couldn't be deleted. Close other apps using them and try again.", error);
        }
    }

    /// <summary>Turns a WAV recording into text.</summary>
    public Task<string> TranscribeAsync(string model, byte[] wav, CancellationToken token) => UseAsync(async http =>
    {
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "speech.wav");
        form.Add(new StringContent(model), "model");
        using var timeout = Timeout(token, TimeSpan.FromMinutes(3));
        using var response = await http.PostAsync("audio/transcriptions", form, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? string.Empty : string.Empty;
    }, token);

    public const int EmbeddingBatch = 32;

    /// <summary>One embedding per input, in order. Inputs are sent in batches.</summary>
    public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken token) => UseAsync(async http =>
    {
        var result = new List<float[]>(inputs.Count);
        for (var start = 0; start < inputs.Count; start += EmbeddingBatch)
        {
            var batch = inputs.Skip(start).Take(EmbeddingBatch).ToArray();
            using var timeout = Timeout(token, TimeSpan.FromMinutes(5));
            using var response = await http.PostAsJsonAsync("embeddings", new { model, input = batch }, timeout.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            result.AddRange(ParseEmbeddings(document.RootElement, batch.Length));
        }
        return result.ToArray();
    }, token);

    /// <summary>Reads an OpenAI-style embeddings response, ordered by each item's index.</summary>
    public static float[][] ParseEmbeddings(JsonElement root, int expected)
    {
        var items = root.GetProperty("data").EnumerateArray()
            .Select((item, position) => (Index: item.TryGetProperty("index", out var index) ? index.GetInt32() : position,
                Vector: item.GetProperty("embedding").EnumerateArray().Select(value => value.GetSingle()).ToArray()))
            .OrderBy(item => item.Index).Select(item => item.Vector).ToArray();
        if (items.Length != expected || items.Any(vector => vector.Length == 0))
            throw new RuntimeException("media_embeddings", "The search engine returned an incomplete answer.");
        return items;
    }

    /// <summary>Speaks text with a Kokoro voice. Returns WAV bytes.</summary>
    public Task<byte[]> SpeakAsync(string model, string voice, string text, CancellationToken token) => UseAsync(async http =>
    {
        using var timeout = Timeout(token, TimeSpan.FromMinutes(3));
        using var response = await http.PostAsJsonAsync("audio/speech",
            new { model, input = text, voice, response_format = "wav" }, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
    }, token);

    /// <summary>Creates one image. Returns PNG bytes.</summary>
    public Task<byte[]> GenerateImageAsync(string model, string prompt, CancellationToken token) => UseAsync(async http =>
    {
        using var timeout = Timeout(token, TimeSpan.FromMinutes(10));
        using var response = await http.PostAsJsonAsync("images/generations",
            new { model, prompt, n = 1, size = "512x512", response_format = "b64_json" }, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
        var data = document.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0 || !data[0].TryGetProperty("b64_json", out var image))
            throw new RuntimeException("media_image", "The image engine returned no picture. Try a different description.");
        return Convert.FromBase64String(image.GetString()!);
    }, token);

    public async Task StopAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _startGate.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Runs one request against the started server and restarts the idle countdown afterwards.</summary>
    private async Task<T> UseAsync<T>(Func<HttpClient, Task<T>> work, CancellationToken token)
    {
        Interlocked.Increment(ref _busy);
        _idle?.Cancel();
        try
        {
            var http = await EnsureStartedAsync(token).ConfigureAwait(false);
            try { return await work(http).ConfigureAwait(false); }
            catch (HttpRequestException error)
            {
                throw new RuntimeException("media_request", "Winference couldn't reach its media engine. Try again.", error);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new RuntimeException("media_timeout", "The media engine took too long to answer. Try again.");
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _busy) == 0) ScheduleIdleStop();
        }
    }

    private void ScheduleIdleStop()
    {
        var idle = new CancellationTokenSource();
        Interlocked.Exchange(ref _idle, idle)?.Cancel();
        _ = Task.Delay(IdleStop, idle.Token).ContinueWith(async task =>
        {
            if (task.IsCanceled || Volatile.Read(ref _busy) > 0) return;
            await _startGate.WaitAsync().ConfigureAwait(false);
            try { if (Volatile.Read(ref _busy) == 0 && !idle.IsCancellationRequested) await StopCoreAsync().ConfigureAwait(false); }
            finally { _startGate.Release(); }
        }, TaskScheduler.Default);
    }

    private async Task<HttpClient> EnsureStartedAsync(CancellationToken token)
    {
        await _startGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_owned is { HasExited: false } && _http is not null) return _http;
            await StopCoreAsync().ConfigureAwait(false);
            if (!File.Exists(_lemonadePath) || await PinnedRuntimeAcquisition.HashAsync(_lemonadePath, token).ConfigureAwait(false) != PinnedRuntimeAcquisition.LemonadeExeSha256)
                throw new RuntimeException("media_runtime", "The bundled Lemonade server is missing or changed. Reinstall Winference.");

            var cache = Path.Combine(_root, "cache");
            var config = Path.Combine(_root, "config");
            var models = Path.Combine(_root, "models");
            Directory.CreateDirectory(cache); Directory.CreateDirectory(config); Directory.CreateDirectory(models);
            await File.WriteAllTextAsync(Path.Combine(config, "config.json"), JsonSerializer.Serialize(new
            {
                config_version = 2, host = "127.0.0.1", broadcast = false, offline = false, no_fetch_executables = false,
                auto_check_model_updates = false, auto_update_models = false, inhibit_suspend = false,
                max_loaded_models = 2, models_dir = models, log_file = "disabled", log_level = "warning",
                telemetry = new { enabled = false }
            }, Options), token).ConfigureAwait(false);

            var port = LoopbackListener.FindCandidatePort();
            var apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(entry => entry.Key is string key && !key.StartsWith("LEMONADE_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            environment["LEMONADE_API_KEY"] = apiKey;
            var owned = OwnedProcessSession.Start(_lemonadePath,
                [cache, config, "--port", port.ToString(), "--host", "127.0.0.1", "--no-broadcast", "--log-file", "disabled"],
                Path.GetDirectoryName(_lemonadePath)!, environment);
            var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v1/"), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _owned = owned;
            _http = http;

            using var deadline = Timeout(token, TimeSpan.FromSeconds(30));
            while (true)
            {
                if (owned.HasExited) throw new RuntimeException("media_start", "The media engine could not start.");
                if (LoopbackListener.IsOwned(port, owned.ProcessId))
                {
                    try
                    {
                        using var response = await http.GetAsync("health", deadline.Token).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                            if (health.RootElement.GetProperty("version").GetString() != Version)
                                throw new RuntimeException("media_runtime", "The media engine version differs from the tested build.");
                            return http;
                        }
                    }
                    catch (HttpRequestException) { }
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _startGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _http?.Dispose();
        _http = null;
        if (_owned is { } owned)
        {
            _owned = null;
            try { await owned.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { owned.Dispose(); }
        }
    }

    private static async Task PostAsync(HttpClient http, string path, object body, CancellationToken token, TimeSpan limit)
    {
        using var timeout = Timeout(token, limit);
        using var response = await http.PostAsJsonAsync(path, body, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            detail = error.ValueKind == JsonValueKind.String ? error.GetString()! : error.GetProperty("message").GetString()!;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        if (detail.Length > 300) detail = detail[..300] + "…";
        throw new RuntimeException("media_request", detail.Length > 0 ? "The media engine said: " + detail : $"The media engine failed (HTTP {(int)response.StatusCode}).");
    }

    private static CancellationTokenSource Timeout(CancellationToken token, TimeSpan limit)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(limit);
        return source;
    }

    private static void RequireKnown(string model)
    {
        if (MediaAddons.Find(model) is null) throw new ArgumentException("Unknown add-on.", nameof(model));
    }

    private void SetInstalled(string model, bool installed)
    {
        lock (_sync)
        {
            var set = Installed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (installed) set.Add(model); else set.Remove(model);
            Directory.CreateDirectory(_root);
            var temporary = _installedPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(set.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
            File.Move(temporary, _installedPath, overwrite: true);
        }
        InstalledChanged?.Invoke(this, EventArgs.Empty);
    }
}
