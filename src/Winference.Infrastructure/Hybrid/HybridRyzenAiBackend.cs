using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Lemonade;
using Winference.Infrastructure.Setup;
using Winference.Infrastructure.Windows;

namespace Winference.Infrastructure.Hybrid;

/// <summary>An owned, private AMD hybrid worker; no external server or fallback is adopted.</summary>
public sealed class HybridRyzenAiBackend(
    HybridModelRegistration registration, HybridRuntimeInstallation runtime,
    string workerExecutable, string sessionsRoot) : IRuntimeBackend
{
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly List<FileStream> _componentLeases = [];
    private OwnedProcessSession? _owned;
    private IDisposable? _modelLease;
    private HttpClient? _http;
    private RuntimeSession? _session;
    private SetupHardware? _hardware;
    private Guid _nonce;
    private int _port;
    private string? _sessionDirectory;
    public string[] LastDiagnostics { get; private set; } = [];
    public string? LastControlError { get; private set; }
    public int? LastPromptTokenCount { get; private set; }

    public async Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "GPU + NPU chat requires native Windows.", "windows_required");
        if (profile != registration.Profile || profile.ExecutionMode != ExecutionMode.GpuNpu)
            return new(false, "This hybrid profile does not match the selected model files.", "hybrid_profile");
        if (!HybridModelService.CanExecute(registration))
            return new(false, HybridModelService.ExecutionStatus(registration), "hybrid_model_unqualified");
        if (!File.Exists(workerExecutable)) return new(false, "The Winference hybrid worker is missing. Restore the complete application folder.", "hybrid_worker_missing");
        if (!await HybridRuntimeAcquisition.VerifyAsync(runtime, token))
            return new(false, "The GPU + NPU component is missing or changed. Review its setup from Models.", "hybrid_runtime_missing");
        var hardware = await WindowsSetupHardware.InspectAsync(token);
        if (!hardware.NpuDetected || !hardware.Graphics.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return new(false, "GPU + NPU execution needs a supported AMD Ryzen AI NPU and Radeon graphics.", "hybrid_hardware");
        _hardware = hardware;
        return await HybridModelService.VerifyAsync(registration, token);
    }

    public async Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (_owned is not null || _modelLease is not null) throw new RuntimeException("hybrid_busy", "The GPU + NPU model is already starting or running.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        try
        {
            LastControlError = null; LastPromptTokenCount = null;
            _modelLease = await HybridModelService.OpenVerifiedReadLeaseAsync(registration, token);
            if (!Directory.Exists(runtime.RuntimeDirectory))
                throw new RuntimeException("hybrid_runtime_missing", "The GPU + NPU component is missing. Review its setup from Models.");
            foreach (var file in Directory.EnumerateFiles(runtime.RuntimeDirectory))
                _componentLeases.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
            var available = await DiscoverAsync(profile, token);
            if (!available.IsAvailable) throw new RuntimeException(available.FailureCode ?? "hybrid_unavailable", available.Detail);
            _nonce = Guid.NewGuid();
            var directory = Path.Combine(Path.GetFullPath(sessionsRoot), "hybrid-" + _nonce.ToString("N"));
            _sessionDirectory = directory;
            Directory.CreateDirectory(directory);
            _port = LoopbackListener.FindCandidatePort();
            var endpoint = new Uri($"http://127.0.0.1:{_port}/v1/");
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(item => item.Key is string key && !key.StartsWith("LEMONADE_", StringComparison.OrdinalIgnoreCase) && key != "WINFERENCE_HYBRID_TOKEN")
                .ToDictionary(item => (string)item.Key, item => (string)item.Value!, StringComparer.OrdinalIgnoreCase);
            environment["WINFERENCE_HYBRID_TOKEN"] = secret;
            var dataRoot = Directory.GetParent(runtime.RuntimeDirectory)?.Parent?.FullName
                ?? throw new RuntimeException("hybrid_runtime_missing", "The hybrid component location is invalid.");
            _owned = OwnedProcessSession.Start(Path.GetFullPath(workerExecutable),
                ["--hybrid-worker", registration.ModelDirectory, dataRoot, _port.ToString(System.Globalization.CultureInfo.InvariantCulture), _nonce.ToString("N")],
                directory, environment);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_owned.HasExited) throw new RuntimeException("hybrid_worker_startup", "The AMD hybrid runtime stopped while loading. Reload the model or choose another mode.");
                if (LoopbackListener.IsOwned(_port, _owned.ProcessId))
                {
                    var proof = await ProveReadyAsync(token);
                    _session = new(_nonce, 0, profile, endpoint, "AMD Ryzen AI 1.7.0", "AMD GPU + NPU", profile.ContextLength, proof);
                    return _session;
                }
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None);
            throw new RuntimeException("hybrid_startup_timeout", "The GPU + NPU model took too long to load. Check available memory, then retry.");
        }
        catch { await StopAsync(CancellationToken.None); throw; }
    }

    public async IAsyncEnumerable<GenerationEvent> GenerateAsync(RuntimeSession session, GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_session is null || _session.SessionId != session.SessionId || session.Profile != registration.Profile ||
            request.ProfileId != session.Profile.ProfileId || request.SessionRevision != session.Revision || _http is null)
            throw new RuntimeException("hybrid_session", "The selected hybrid session changed. Load it again before sending.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        _ = await ProveReadyAsync(token);
        var promptCount = await CountPromptTokensAsync(session, request.Messages, token);
        LastPromptTokenCount = promptCount.Tokens;
        if ((long)LastPromptTokenCount + request.MaxOutputTokens > session.EffectiveContextLength)
            throw new RuntimeException("hybrid_context", "This conversation and reply exceed the selected hybrid context.");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(session.Endpoint, "chat/completions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = session.Profile.ProviderModelId, messages = request.Messages.Select(item => new { role = item.Role, content = item.Content }),
                stream = true, max_tokens = request.MaxOutputTokens, expected_prompt_tokens = LastPromptTokenCount,
                temperature = request.Temperature
            }), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            using var errorStream = await response.Content.ReadAsStreamAsync(token);
            var bytes = new byte[1024]; var count = await errorStream.ReadAsync(bytes, token);
            LastControlError = Encoding.UTF8.GetString(bytes, 0, count);
            throw new RuntimeException("hybrid_request", "The hybrid request was refused. Shorten the conversation or reload the selected model.");
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new RuntimeException("hybrid_stream", "The hybrid worker did not return a supported streamed reply.");
        var usageSeen = false;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await foreach (var item in CompletionStreamReader.ReadAsync(stream, token))
        {
            if (item.Kind == GenerationEventKind.Usage)
            {
                if (item.Usage?.PromptTokens != LastPromptTokenCount || item.Usage.CompletionTokens is not { } output || output < 0 || output > request.MaxOutputTokens)
                    throw new RuntimeException("hybrid_tokenizer", "The native token usage differs from the selected hybrid profile.");
                usageSeen = true;
            }
            if (item.Kind == GenerationEventKind.Completed)
            {
                if (!usageSeen || item.FinishReason is not ("stop" or "length"))
                    throw new RuntimeException("hybrid_stream", "The hybrid reply did not confirm its completion and token usage.");
                _ = await ProveReadyAsync(token);
            }
            yield return item;
        }
    }

    public async Task<PromptTokenCount> CountPromptTokensAsync(RuntimeSession session,
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (_session is null || _session.SessionId != session.SessionId || session.Profile != registration.Profile || _http is null)
            throw new RuntimeException("hybrid_session", "The selected hybrid session changed. Load it again before sending.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        _ = await ProveReadyAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(session.Endpoint, "tokenize"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = session.Profile.ProviderModelId, max_tokens = session.Profile.MaxOutputTokens,
                messages = messages.Select(item => new { role = item.Role, content = item.Content })
            }), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new RuntimeException("hybrid_tokenizer", "The selected hybrid worker could not count this conversation. Reload it and retry.");
        using var budget = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        var value = budget.RootElement;
        if (value.GetProperty("session").GetString() != _nonce.ToString("N") ||
            value.GetProperty("model_id").GetString() != registration.ModelId ||
            value.GetProperty("context_length").GetInt32() != session.EffectiveContextLength)
            throw new RuntimeException("hybrid_tokenizer", "The selected hybrid tokenizer or session changed.");
        var count = value.GetProperty("prompt_tokens").GetInt32();
        if (count < 1) throw new RuntimeException("hybrid_tokenizer", "The hybrid worker returned an invalid token count.");
        return new(count, true);
    }
}
