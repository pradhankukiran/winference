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
}
