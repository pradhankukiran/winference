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
}
