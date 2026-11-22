using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Infrastructure.Lemonade;
using Winference.Infrastructure.Setup;
using Winference.Infrastructure.Windows;

namespace Winference.Infrastructure.Npu;

/// <summary>
/// Runs the selected NPU model in one app-owned FastFlowLM server, started directly with no
/// supervisor in between. Never connects to, unloads, or stops a FastFlowLM the user started.
/// </summary>
public sealed class FastFlowLmBackend(RuntimeInstallation installation) : IRuntimeBackend
{
    /// <summary>Tokens left unused when a reply may fill the context, covering small template differences.</summary>
    private const int ReplyMargin = 32;
    private OwnedProcessSession? _owned;
    private HttpClient? _http;
    private RuntimeSession? _session;
    private int _port;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private LocalModelTokenizer? _contextBudget;
    public string[] LastDiagnostics { get; private set; } = [];

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
            var tokenizerPath = Path.Combine(installation.ModelDirectory, "tokenizer.json");
            var tokenizerConfigPath = Path.Combine(installation.ModelDirectory, "tokenizer_config.json");
            if (File.Exists(tokenizerPath))
            {
                try { _contextBudget ??= new LocalModelTokenizer(tokenizerPath, tokenizerConfigPath); }
                catch (Exception) { _contextBudget = null; }
            }
            if (_owned is not null) throw new RuntimeException("already_started", "The AI runtime is already starting or running.");
            var sessionId = Guid.NewGuid();
            _port = LoopbackListener.FindCandidatePort();
            _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{_port}/"), Timeout = Timeout.InfiniteTimeSpan };
            // FastFlowLM loads the model before it starts listening, so a listener means the model is in place.
            _owned = OwnedProcessSession.Start(installation.FlmPath, LaunchArguments(profile.ContextLength),
                Path.GetDirectoryName(installation.FlmPath)!, installation.CreateEnvironment());
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_owned.HasExited) throw new RuntimeException("startup_failed", "The NPU engine could not start. Check the NPU driver and retry.");
                if (LoopbackListener.IsOwned(_port, _owned.ProcessId))
                {
                    try
                    {
                        using var version = await GetJsonAsync("api/version", token).ConfigureAwait(false);
                        if (version.RootElement.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String &&
                            value.GetString()!.TrimStart('v') == PinnedRuntimeAcquisition.FlmVersion) break;
                        throw new RuntimeException("runtime_mismatch", "The NPU engine version differs from the tested build.");
                    }
                    catch (HttpRequestException) { }
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            var proof = await ProveReadyAsync(profile, token).ConfigureAwait(false);
            _session = new RuntimeSession(sessionId, 0, profile, new Uri($"http://127.0.0.1:{_port}/v1/"),
                "FastFlowLM " + PinnedRuntimeAcquisition.FlmVersion, "AMD NPU", profile.ContextLength, proof);
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

    /// <summary>
    /// Serves only this model on loopback. FastFlowLM has no API key, so browser preflight is turned off
    /// and the model is named in every request: any other name would make it load or download that model.
    /// </summary>
    public string[] LaunchArguments(int contextLength) =>
        ["serve", installation.Checkpoint, "--ctx-len", contextLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
         "--host", "127.0.0.1", "--port", _port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--cors", "0", "--quiet"];

    /// <summary>Confirms the engine is the process this backend started, still owns its port, and serves the selected model.</summary>
    private async Task<string> ProveReadyAsync(ModelProfile profile, CancellationToken token)
    {
        var owned = _owned;
        if (owned is null || owned.HasExited || !LoopbackListener.IsOwned(_port, owned.ProcessId) ||
            !SamePath(owned.GetImagePath(owned.ProcessId), installation.FlmPath))
            throw new RuntimeException("engine_ownership", "The app could not confirm ownership of the selected NPU engine.");
        using var running = await GetJsonAsync("api/ps", token).ConfigureAwait(false);
        if (!running.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array ||
            models.GetArrayLength() != 1 || !models[0].TryGetProperty("model", out var model) || model.GetString() != installation.Checkpoint)
            throw new RuntimeException("model_identity", "The loaded model differs from your selection.");
        return JsonSerializer.Serialize(new
        {
            enginePid = owned.ProcessId, engineStartUtc = owned.StartedUtc, engineOwnsListener = true, device = "npu",
            checkpoint = installation.Checkpoint, context = profile.ContextLength, profileId = profile.ProfileId,
            artifactId = profile.ArtifactId, launchCommand = LaunchArguments(profile.ContextLength), port = _port
        });
    }

    public async IAsyncEnumerable<GenerationEvent> GenerateAsync(RuntimeSession session, GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_session is null || session.SessionId != _session.SessionId || session.Profile != installation.Profile ||
            request.ProfileId != session.Profile.ProfileId || request.SessionRevision != session.Revision || _owned is null || _owned.HasExited)
            throw new RuntimeException("stale_session", "The AI session changed. Load the model again before sending.");
        _contextBudget?.Validate(request.Messages, request.MaxOutputTokens, session.EffectiveContextLength);
        var maxTokens = ReplyLimit(request, session.EffectiveContextLength);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        // Recheck the engine before every request.
        _ = await ProveReadyAsync(session.Profile, deadline.Token).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = installation.Checkpoint,
                messages = request.Messages.Select(ToWire),
                stream = true, max_tokens = maxTokens, temperature = request.Temperature,
                stream_options = new { include_usage = true }
            })
        };
        using var response = await _http!.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw MapHttpError((int)response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new RuntimeException("stream_format", "The NPU engine returned an unexpected reply format.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await foreach (var item in CompletionStreamReader.ReadAsync(stream, deadline.Token).ConfigureAwait(false)) yield return item;
    }

    /// <summary>
    /// The profile's limit, or everything the prompt leaves free when the request allows it.
    /// Without the model's tokenizer the prompt size is unknown, so the profile's limit is kept.
    /// </summary>
    private int ReplyLimit(GenerationRequest request, int contextLength)
    {
        if (!request.UseRemainingContext || _contextBudget is null) return request.MaxOutputTokens;
        var images = request.Messages.Sum(message => message.Images?.Count ?? 0);
        var prompt = (long)_contextBudget.CountPromptTokens(request.Messages) + images * (long)ChatImage.TokenBudget;
        return (int)Math.Max(request.MaxOutputTokens, contextLength - prompt - ReplyMargin);
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
        _ = await ProveReadyAsync(session.Profile, cancellationToken).ConfigureAwait(false);
        if (_contextBudget is not null)
        {
            // Text is counted exactly, plus one token for FastFlowLM's prefill boundary.
            // Each picture adds a fixed allowance, so the total is an estimate.
            var images = messages.Sum(message => message.Images?.Count ?? 0);
            return new(checked(_contextBudget.CountPromptTokens(messages) + 1 + images * ChatImage.TokenBudget), images == 0);
        }
        return PromptTokenCount.Estimate(messages);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_owned is not null)
            {
                // Cancelling an HTTP stream alone does not settle NPU work. Terminate
                // only the retained job, then wait for every owned descendant to exit.
                await _owned.StopAsync(CancellationToken.None).ConfigureAwait(false);
                LastDiagnostics = _owned.Diagnostics;
                _owned.Dispose(); _owned = null;
            }
            _contextBudget?.Dispose();
            _contextBudget = null;
            _session = null;
            _http?.Dispose(); _http = null;
        }
        finally { _stopGate.Release(); }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken token)
    {
        var client = _http ?? throw new RuntimeException("session_stopped", "The AI session has stopped. Connect again.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw MapHttpError((int)response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    private static RuntimeException MapHttpError(int code) => code switch
    {
        400 or 413 or 422 => new("request_rejected", "The model could not accept this request. Shorten the message or start a new chat; check the selected model's context limit."),
        _ => new("runtime_request", "The local AI request failed. Check the runtime and retry.")
    };
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
