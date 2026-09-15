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

    private string ProveReady(JsonElement health, ModelProfile profile, int port)
    {
        if (_owned is null || _owned.HasExited || !LoopbackListener.IsOwned(port, _owned.ProcessId))
            throw new RuntimeException("endpoint_ownership", "The app could not confirm ownership of its AI server.");
        var models = health.GetProperty("all_models_loaded").EnumerateArray().ToArray();
        var expectedPublicId = profile.ProviderModelId.StartsWith("user.", StringComparison.Ordinal) ? profile.ProviderModelId[5..] : profile.ProviderModelId;
        var model = models.SingleOrDefault(x => x.GetProperty("model_name").GetString() is string id &&
            (id == profile.ProviderModelId || id == expectedPublicId));
        if (model.ValueKind != JsonValueKind.Object || model.GetProperty("checkpoint").GetString() != installation.Checkpoint ||
            model.GetProperty("recipe").GetString() != "flm" || model.GetProperty("device").GetString() != "npu" ||
            model.GetProperty("recipe_options").GetProperty("ctx_size").GetInt32() != profile.ContextLength)
            throw new RuntimeException("model_identity", "The loaded model, NPU route, or context differs from your selection.");
        var enginePid = model.GetProperty("pid").GetInt32();
        var command = model.GetProperty("launch_command").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (!_owned.GetProcessIds().Contains(enginePid) || !SamePath(_owned.GetImagePath(enginePid), installation.FlmPath) ||
            command.Length < 3 || !SamePath(command[0], installation.FlmPath) || command[1] != "serve" ||
            command[2] != installation.Checkpoint || !HasPair(command, "--ctx-len", profile.ContextLength.ToString()) ||
            !HasPair(command, "--host", "127.0.0.1"))
            throw new RuntimeException("engine_ownership", "The app could not confirm ownership of the selected NPU engine.");
        if (!Uri.TryCreate(model.GetProperty("backend_url").GetString(), UriKind.Absolute, out var backend) ||
            backend.Scheme != "http" || backend.Host != "127.0.0.1" || !LoopbackListener.IsOwned(backend.Port, enginePid))
            throw new RuntimeException("engine_endpoint", "The selected NPU engine does not own its local connection.");
        using var engine = Process.GetProcessById(enginePid);
        return JsonSerializer.Serialize(new
        {
            supervisorPid = _owned.ProcessId, supervisorStartUtc = _owned.StartedUtc,
            enginePid, engineStartUtc = engine.StartTime.ToUniversalTime(),
            jobContainsEngine = true, supervisorOwnsListener = true, engineOwnsListener = true,
            recipe = "flm", device = "npu", checkpoint = installation.Checkpoint,
            context = profile.ContextLength, profileId = profile.ProfileId, artifactId = profile.ArtifactId,
            launchCommand = command, backendPort = backend.Port
        });
    }

    public async IAsyncEnumerable<GenerationEvent> GenerateAsync(RuntimeSession session, GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_session is null || session.SessionId != _session.SessionId || session.Profile != installation.Profile ||
            request.ProfileId != session.Profile.ProfileId || request.SessionRevision != session.Revision || _owned is null || _owned.HasExited)
            throw new RuntimeException("stale_session", "The AI session changed. Load the model again before sending.");
        _contextBudget?.Validate(request.Messages, request.MaxOutputTokens, session.EffectiveContextLength);
        // Recheck the served route and owned processes before every request.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using (var health = await GetJsonAsync("health", deadline.Token).ConfigureAwait(false))
            _ = ProveReady(health.RootElement, session.Profile, session.Endpoint.Port);
        using var message = new HttpRequestMessage(HttpMethod.Post, ApiUri(session.Endpoint, "chat/completions"))
        {
            Content = JsonContent.Create(new
            {
                model = session.Profile.ProviderModelId,
                messages = request.Messages.Select(ToWire),
                stream = true, max_tokens = request.MaxOutputTokens, temperature = request.Temperature,
                stream_options = new { include_usage = true }
            })
        };
        using var response = await _http!.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw MapHttpError((int)response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new RuntimeException("stream_format", "The AI server returned an unexpected reply format.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await foreach (var item in CompletionStreamReader.ReadAsync(stream, deadline.Token).ConfigureAwait(false)) yield return item;
    }

    /// <summary>Plain text, or OpenAI content parts (text, then images as data URLs) for a vision model.</summary>
    public static object ToWire(ChatMessage message) => message.HasImages
        ? new
        {
            role = message.Role,
            content = new object[] { new { type = "text", text = message.Content } }
                .Concat(message.Images!.Select(image => (object)new { type = "image_url", image_url = new { url = image.DataUrl } })).ToArray()
        }
        : new { role = message.Role, content = (object)message.Content };

    public async Task<PromptTokenCount> CountPromptTokensAsync(RuntimeSession session,
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (_session is null || session.SessionId != _session.SessionId || session.Profile != _session.Profile || _http is null)
            throw new RuntimeException("session_stopped", "The selected NPU session changed. Load it again before sending.");
        cancellationToken.ThrowIfCancellationRequested();
        using (var health = await GetJsonAsync("health", cancellationToken).ConfigureAwait(false))
            _ = ProveReady(health.RootElement, session.Profile, session.Endpoint.Port);
        if (_contextBudget is not null)
        {
            // Charge the model's exact rendered prompt + 1 extra token for FastFlowLM prefill boundary
            // Text is counted exactly; each picture adds a fixed allowance, so the total is an estimate.
            var images = messages.Sum(message => message.Images?.Count ?? 0);
            return new(checked(_contextBudget.CountPromptTokens(messages) + 1 + images * ChatImage.TokenBudget), images == 0);
        }
        // Fall back to conservative estimate if local tokenizer is unavailable
        return PromptTokenCount.Estimate(messages);
    }
}
