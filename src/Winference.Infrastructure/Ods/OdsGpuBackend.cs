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
/// Implementation only: GPU behavior has not been exercised yet.
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
}
