using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Winference.Core.Gpu;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Infrastructure.Gpu;
using Winference.Infrastructure.Lemonade;
using Winference.Infrastructure.Windows;

namespace Winference.Infrastructure.Ods;

/// <summary>
/// One app-owned AMD Vulkan llama.cpp session. The attributed ODS helper performs
/// argument, runtime, device and model proofs; the Windows job owns its supervisor
/// and native children. A separately running server is never adopted.
/// </summary>
public sealed class OdsGpuBackend(
    GpuModelRegistration registration,
    GpuRuntimeInstallation runtime,
    string helperDirectory,
    string sessionsRoot) : IRuntimeBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private OwnedProcessSession? _owned;
    private FileStream? _modelLease;
    private HttpClient? _http;
    private RuntimeSession? _session;
    private int _runtimeProcessId;
    private int _port;
    private string? _sessionDirectory;

    public string[] LastDiagnostics { get; private set; } = [];

    public async Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "GPU chat requires native Windows.", "windows_required");
        if (profile != registration.Profile || profile.ExecutionMode != ExecutionMode.GpuOnly)
            return new(false, "This GPU model profile does not match the selected file.", "gpu_profile_mismatch");
        if (profile.ContextLength is < 256 or > 131072)
            return new(false, "This GPU build supports a context between 256 and 131,072 tokens.", "gpu_context");
        if (profile.ProviderModelId != Path.GetFileName(registration.ModelPath))
            return new(false, "The GPU model alias differs from the selected GGUF filename. Select the file again.", "gpu_alias");
        if (!File.Exists(PowerShellPath) || !File.Exists(BridgePath) ||
            !File.Exists(Path.Combine(helperDirectory, "native-llama-runtime.ps1")) ||
            !File.Exists(Path.Combine(helperDirectory, "private-file.ps1")))
            return new(false, "The native GPU helper is missing. Restore the complete Winference application folder.", "gpu_helper_missing");
        if (!await GpuRuntimeAcquisition.VerifyAsync(runtime, cancellationToken).ConfigureAwait(false))
            return new(false, "The GPU runtime is missing or changed. Prepare the GPU component from Models before loading.", "gpu_runtime_missing");
        return await GpuModelService.VerifyAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new RuntimeException("windows_required", "GPU chat requires native Windows.");
        if (_owned is not null || _modelLease is not null)
            throw new RuntimeException("gpu_busy", "The GPU runtime is already starting or running.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        try
        {
            // The same retained read handle spans verification and the session.
            // A writer or replacement cannot change this external model behind the proof.
            _modelLease = new FileStream(registration.ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var availability = await DiscoverAsync(profile, token).ConfigureAwait(false);
            if (!availability.IsAvailable)
                throw new RuntimeException(availability.FailureCode ?? "gpu_unavailable", availability.Detail);

            var sessionId = Guid.NewGuid();
            _sessionDirectory = Path.Combine(Path.GetFullPath(sessionsRoot), "gpu-" + sessionId.ToString("N"));
            Directory.CreateDirectory(_sessionDirectory);
            _port = LoopbackListener.FindCandidatePort();
            var requestPath = Path.Combine(_sessionDirectory, "request.json");
            var readyPath = Path.Combine(_sessionDirectory, "ready.json");
            var failurePath = Path.Combine(_sessionDirectory, "failure.json");
            WritePrivateRequest(requestPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                sessionId = sessionId.ToString("N"),
                executablePath = Path.GetFullPath(runtime.ExecutablePath),
                modelPath = Path.GetFullPath(registration.ModelPath),
                modelSha256 = registration.Sha256,
                modelLengthBytes = registration.LengthBytes,
                contextSize = profile.ContextLength,
                port = _port,
                releaseTag = runtime.ReleaseTag,
                archiveSha256 = runtime.ArchiveSha256
            }, JsonOptions));
            token.ThrowIfCancellationRequested();
            _owned = OwnedProcessSession.Start(PowerShellPath,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", BridgePath, "-RequestPath", requestPath],
                Path.GetFullPath(helperDirectory), CleanEnvironment());

            OdsReadyRecord ready;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(failurePath)) throw await ReadFailureAsync(failurePath, token).ConfigureAwait(false);
                if (_owned is null || _owned.HasExited)
                    throw new RuntimeException("gpu_startup", "The GPU helper stopped before the model became ready. Check the GPU driver and model file, then retry.");
                if (File.Exists(readyPath))
                {
                    ready = await ReadReadyAsync(readyPath, token).ConfigureAwait(false);
                    ProveOwnedRecord(ready, sessionId, profile);
                    break;
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            var keyPath = Path.Combine(_sessionDirectory, "api.key");
            if (new FileInfo(keyPath).Length != 64)
                throw new RuntimeException("gpu_key", "The local GPU connection key could not be verified. Retry loading the model.");
            var apiKey = await File.ReadAllTextAsync(keyPath, Encoding.ASCII, token).ConfigureAwait(false);
            if (apiKey.Length != 64 || apiKey.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new RuntimeException("gpu_key", "The local GPU connection key could not be verified. Retry loading the model.");
            var endpoint = new Uri($"http://127.0.0.1:{_port}/");
            _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            await ProveHttpAsync(ready, profile, token).ConfigureAwait(false);
            ProveOwnedRecord(ready, sessionId, profile);
            _runtimeProcessId = ready.ProcessId;
            var proof = JsonSerializer.Serialize(new
            {
                sessionId, supervisorPid = _owned!.ProcessId, supervisorStartUtc = _owned.StartedUtc,
                runtimePid = ready.ProcessId, runtimeStartUtc = ready.StartedUtc,
                runtimePath = ready.ExecutablePath, runtimeTag = ready.ReleaseTag, ready.Commit,
                modelPath = ready.ModelPath, modelSha256 = ready.ModelSha256,
                modelAlias = ready.ModelId, requestedContext = ready.ContextSize, runtimeContext = ready.RuntimeContext,
                ready.Device, ready.DeviceDescription, ready.AdapterPnpId, ready.DriverVersion,
                ready.OffloadedLayers, ready.TotalLayers, port = _port
            }, JsonOptions);
            _session = new RuntimeSession(sessionId, 0, profile, endpoint, "llama.cpp " + runtime.ReleaseTag,
                "GPU · " + ready.DeviceDescription, profile.ContextLength, proof);
            return _session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new RuntimeException("gpu_startup_timeout", "The GPU model took too long to load. Check the driver and available memory, then retry.");
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<GenerationEvent> GenerateAsync(RuntimeSession session, GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_session is null || session.SessionId != _session.SessionId || session.Profile != registration.Profile ||
            request.ProfileId != registration.Profile.ProfileId || _http is null)
            throw new RuntimeException("gpu_session", "The selected GPU session changed. Reload the model before sending.");
        AssertCurrentListener();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        // Model-specific rendering/counting is performed by the selected runtime.
        // HTTP request limits remain transport bounds, never invented token counts.
        await ValidateContextAsync(request, token).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = registration.Profile.ProviderModelId,
                messages = request.Messages.Select(item => new { role = item.Role, content = item.Content }),
                max_tokens = request.MaxOutputTokens,
                temperature = request.Temperature,
                stream = true,
                stream_options = new { include_usage = true }
            })
        };
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new RuntimeException("gpu_completion", response.StatusCode == System.Net.HttpStatusCode.BadRequest
                ? "The GPU runtime rejected this request or its context limit. Shorten the message or start a new chat."
                : "The local GPU runtime could not produce a reply. Retry loading the model.");
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new RuntimeException("gpu_stream", "The GPU runtime did not return a supported streamed reply.");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await foreach (var item in CompletionStreamReader.ReadAsync(stream, token).ConfigureAwait(false))
        {
            if (item.Kind == GenerationEventKind.Completed)
            {
                if (item.FinishReason is not ("stop" or "length"))
                    throw new RuntimeException("gpu_stream", "The GPU reply ended without a supported completion reason.");
                AssertCurrentListener();
            }
            yield return item;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session = null;
            _http?.Dispose();
            _http = null;
            if (_owned is { } owned)
            {
                LastDiagnostics = owned.Diagnostics;
                await owned.StopAsync(cancellationToken).ConfigureAwait(false);
                owned.Dispose();
                _owned = null;
            }
            _modelLease?.Dispose();
            _modelLease = null;
            _runtimeProcessId = 0;
        }
        finally { _stopGate.Release(); }
    }

    private string BridgePath => Path.GetFullPath(Path.Combine(helperDirectory, "Invoke-GpuRuntime.ps1"));
    private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private void AssertCurrentListener()
    {
        if (_owned is null || _owned.HasExited || !_owned.GetProcessIds().Contains(_runtimeProcessId) ||
            !SamePath(_owned.GetImagePath(_runtimeProcessId), runtime.ExecutablePath) ||
            !LoopbackListener.IsOwned(_port, _runtimeProcessId))
            throw new RuntimeException("gpu_ownership", "The selected GPU runtime no longer owns this connection. Reload the model.");
    }

    private void ProveOwnedRecord(OdsReadyRecord record, Guid sessionId, ModelProfile profile)
    {
        if (_owned is null || _owned.HasExited || record.SchemaVersion != 1 ||
            record.SessionId != sessionId.ToString("N") || record.SupervisorId != _owned.ProcessId ||
            record.Port != _port || record.ModelId != profile.ProviderModelId ||
            !SamePath(record.ModelPath, registration.ModelPath) || record.ModelSha256 != registration.Sha256 ||
            record.ModelLengthBytes != registration.LengthBytes || !SamePath(record.ExecutablePath, runtime.ExecutablePath) ||
            record.ReleaseTag != runtime.ReleaseTag || record.Build != 9014 ||
            record.ContextSize != profile.ContextLength || record.RuntimeContext < profile.ContextLength ||
            record.RuntimeContext - profile.ContextLength >= 256 ||
            record.OffloadedLayers <= 0 || record.OffloadedLayers != record.TotalLayers ||
            !System.Text.RegularExpressions.Regex.IsMatch(record.Device ?? string.Empty, "^Vulkan[0-9]{1,2}$") ||
            !record.AdapterPnpId.StartsWith("PCI\\VEN_1002&", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(record.DeviceDescription) || string.IsNullOrWhiteSpace(record.DriverVersion) ||
            !_owned.GetProcessIds().Contains(record.ProcessId) ||
            !SamePath(_owned.GetImagePath(record.ProcessId), runtime.ExecutablePath) ||
            !LoopbackListener.IsOwned(_port, record.ProcessId))
            throw new RuntimeException("gpu_readiness", "The selected GPU, model, owned process or layer placement could not be verified.");
        using var process = Process.GetProcessById(record.ProcessId);
        if (!DateTimeOffset.TryParse(record.StartedUtc, out var started) ||
            Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalMilliseconds) >= 1)
            throw new RuntimeException("gpu_ownership", "The GPU process identity changed during startup. Retry loading the model.");
    }

    private async Task ProveHttpAsync(OdsReadyRecord record, ModelProfile profile, CancellationToken token)
    {
        using (await GetJsonAsync("health", token).ConfigureAwait(false)) { }
        using var catalog = await GetJsonAsync("v1/models", token).ConfigureAwait(false);
        var models = catalog.RootElement.GetProperty("data");
        if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() != 1 ||
            models[0].GetProperty("id").GetString() != profile.ProviderModelId)
            throw new RuntimeException("gpu_model_identity", "The GPU runtime serves a different model from your selection.");
        using var properties = await GetJsonAsync("props", token).ConfigureAwait(false);
        var settings = properties.RootElement;
        var modelPath = settings.GetProperty("model_path").GetString();
        var context = settings.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32();
        if ((!SamePath(modelPath, registration.ModelPath) && !SamePath(modelPath, record.ModelArgumentPath)) ||
            settings.GetProperty("total_slots").GetInt32() != 1 || context != record.RuntimeContext ||
            context < profile.ContextLength || context - profile.ContextLength >= 256)
            throw new RuntimeException("gpu_model_identity", "The GPU model path, slot count or context differs from your selection.");
    }

    public async Task<PromptTokenCount> CountPromptTokensAsync(RuntimeSession session,
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (_session is null || session.SessionId != _session.SessionId || session.Profile != registration.Profile || _http is null)
            throw new RuntimeException("gpu_session", "The selected GPU session changed. Reload the model before sending.");
        AssertCurrentListener();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var rendered = await PostJsonAsync("apply-template", new
        {
            messages = messages.Select(item => new { role = item.Role, content = item.Content })
        }, token).ConfigureAwait(false);
        var prompt = rendered.RootElement.GetProperty("prompt").GetString()
            ?? throw new RuntimeException("gpu_context", "The GPU runtime could not render this model's chat template.");
        using var tokenized = await PostJsonAsync("tokenize", new
        {
            content = prompt, add_special = false, parse_special = true
        }, token).ConfigureAwait(false);
        var tokens = tokenized.RootElement.GetProperty("tokens");
        if (tokens.ValueKind != JsonValueKind.Array || tokens.GetArrayLength() == 0)
            throw new RuntimeException("gpu_context", "The GPU runtime did not return a valid token count.");
        AssertCurrentListener();
        return new(tokens.GetArrayLength(), true);
    }

    private async Task ValidateContextAsync(GenerationRequest request, CancellationToken token)
    {
        var count = await CountPromptTokensAsync(_session!, request.Messages, token).ConfigureAwait(false);
        if ((long)count.Tokens + request.MaxOutputTokens > registration.Profile.ContextLength)
            throw new RuntimeException("gpu_context", "This conversation and reply reserve exceed the selected context. Shorten the message or start a new chat.");
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendJsonAsync(request, token).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        return await SendJsonAsync(request, token).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (_http is null) throw new RuntimeException("gpu_session", "The GPU connection is unavailable. Reload the model.");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new RuntimeException("gpu_protocol", "The pinned GPU runtime rejected a required model or context check. Reload the model.");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > 4 * 1024 * 1024)
                throw new RuntimeException("gpu_protocol", "The GPU runtime returned oversized metadata.");
            await memory.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
    }

    private static async Task<OdsReadyRecord> ReadReadyAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length is <= 0 or > 65536)
            throw new RuntimeException("gpu_readiness", "The GPU readiness record was invalid.");
        return JsonSerializer.Deserialize<OdsReadyRecord>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), JsonOptions)
            ?? throw new RuntimeException("gpu_readiness", "The GPU readiness record was empty.");
    }

    private static async Task<RuntimeException> ReadFailureAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length is <= 0 or > 65536)
            return new("gpu_startup", "The GPU helper could not start the selected model.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
        var message = document.RootElement.TryGetProperty("message", out var value) ? value.GetString() : null;
        return new("gpu_startup", string.IsNullOrWhiteSpace(message) || message.Length > 2048
            ? "The GPU helper could not start the selected model. Check the GPU driver and available memory."
            : message);
    }

    [SupportedOSPlatform("windows")]
    private static void WritePrivateRequest(string path, byte[] content)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        using var stream = FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew,
            FileSystemRights.FullControl, FileShare.None, 8192, FileOptions.None, security);
        stream.Write(content);
        stream.Flush(true);
    }

    private static Dictionary<string, string> CleanEnvironment() => Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .Where(item => item.Key is string key &&
            !key.StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("LLAMA_LOG_", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(item => (string)item.Key, item => (string)item.Value!, StringComparer.OrdinalIgnoreCase);

    private static bool SamePath(string? left, string? right) => !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
