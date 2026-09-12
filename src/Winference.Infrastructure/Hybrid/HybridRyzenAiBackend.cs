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
}
