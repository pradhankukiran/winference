using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Infrastructure.Windows;

namespace Winference.Infrastructure.Lemonade;

/// <summary>One isolated Lemonade supervisor using the qualified, installed FLM engine.
/// Never connects to, unloads, or terminates an external server.</summary>
public sealed class LemonadeBackend(RuntimeInstallation installation) : IRuntimeBackend
{
    private OwnedProcessSession? _owned;
    private HttpClient? _http;
    private RuntimeSession? _session;
    private string? _sessionDirectory;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private LocalModelTokenizer? _contextBudget;
    public string[] LastDiagnostics { get; private set; } = [];
    public string? LastControlError { get; private set; }

    public Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken cancellationToken) =>
        installation.VerifyAsync(profile, cancellationToken);

    public async Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        try
        {
            var availability = await DiscoverAsync(profile, token).ConfigureAwait(false);
            if (!availability.IsAvailable) throw new RuntimeException(availability.FailureCode!, availability.Detail);
            // Initialize local tokenizer if tokenizer.json exists in the installed model directory
            var tokenizerPath = Path.Combine(installation.ModelDirectory, "tokenizer.json");
            var tokenizerConfigPath = Path.Combine(installation.ModelDirectory, "tokenizer_config.json");
            if (File.Exists(tokenizerPath))
            {
                try
                {
                    _contextBudget ??= new LocalModelTokenizer(tokenizerPath, tokenizerConfigPath);
                }
                catch (Exception)
                {
                    _contextBudget = null;
                }
            }
            if (_owned is not null) throw new RuntimeException("already_started", "The AI runtime is already starting or running.");
            var sessionId = Guid.NewGuid();
            _sessionDirectory = Path.Combine(installation.SessionsRoot, sessionId.ToString("N"));
            var cache = Path.Combine(_sessionDirectory, "cache");
            var config = Path.Combine(_sessionDirectory, "config");
            Directory.CreateDirectory(cache); Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config.json"), JsonSerializer.Serialize(new
            {
                config_version = 2, host = "127.0.0.1", broadcast = false, ctx_size = profile.ContextLength,
                global_timeout = 110, max_loaded_models = 1, offline = true, no_fetch_executables = true,
                auto_check_model_updates = false, auto_update_models = false, inhibit_suspend = false,
                log_file = "disabled", log_level = "warning", flm = new { prefer_system = true },
                telemetry = new { enabled = false }
            }), token).ConfigureAwait(false);
            var port = LoopbackListener.FindCandidatePort();
            var endpoint = new Uri($"http://127.0.0.1:{port}/api/v1/");
            var apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _owned = OwnedProcessSession.Start(installation.LemonadePath,
                [cache, config, "--port", port.ToString(), "--host", "127.0.0.1", "--no-broadcast", "--log-file", "disabled"],
                Path.GetDirectoryName(installation.LemonadePath)!, installation.CreateEnvironment(apiKey));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_owned.HasExited) throw new RuntimeException("startup_failed", "The AI runtime could not start. Check the runtime installation and retry.");
                if (LoopbackListener.IsOwned(port, _owned.ProcessId))
                {
                    try
                    {
                        using var health = await GetJsonAsync("health", token).ConfigureAwait(false);
                        if (health.RootElement.GetProperty("version").GetString() != installation.LemonadeVersion)
                            throw new RuntimeException("runtime_mismatch", "The AI server version differs from the tested build.");
                        break;
                    }
                    catch (HttpRequestException) { }
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }

            // Use FLM's own discovered installed identity. Custom registration can
            // report downloaded=false and cause /load to refresh even existing files.
            // Never call load unless the pinned supervisor recognizes the cache.
            using (var catalog = await GetJsonAsync("models", token).ConfigureAwait(false))
            {
                var model = catalog.RootElement.GetProperty("data").EnumerateArray()
                    .SingleOrDefault(x => x.GetProperty("id").GetString() == profile.ProviderModelId);
                if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("downloaded", out var downloaded) || !downloaded.GetBoolean())
                    throw new RuntimeException("cache_unrecognized", "The AI server did not recognize the existing model files. No download was started.");
            }
            using (await PostJsonAsync("load", new { model_name = profile.ProviderModelId, ctx_size = profile.ContextLength }, token).ConfigureAwait(false)) { }
            using var loaded = await GetJsonAsync("health", token).ConfigureAwait(false);
            var proof = ProveReady(loaded.RootElement, profile, port);
            _session = new RuntimeSession(sessionId, 0, profile, endpoint, "FastFlowLM 1.0.10", "AMD NPU",
                profile.ContextLength, proof);
            return _session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new RuntimeException("startup_timeout", "The model took too long to start. Check the NPU driver and retry.");
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
