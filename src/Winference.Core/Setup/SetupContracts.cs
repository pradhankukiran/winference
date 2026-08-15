using Winference.Core.Models;
using Winference.Core.Runtime;

namespace Winference.Core.Setup;

public sealed record SetupModel(
    string Id, string DisplayName, string Publisher, string Description,
    long? DownloadBytes, long? StorageBytes, string InstallationState,
    string? ExistingModelRoot, string? UnavailableReason,
    int MaximumContextLength, int SuggestedContextLength, string CatalogRevision)
{
    public bool CanSelect => UnavailableReason is null;
    public bool IsInstalled => InstallationState == "Installed";
    public string SizeLabel => IsInstalled
        ? StorageBytes is { } stored ? $"{stored / 1_000_000_000d:0.0} GB on disk" : "Existing download"
        : DownloadBytes is { } bytes ? $"{bytes / 1_000_000_000d:0.0} GB download" : "Download size unavailable";
    public string AvailabilityLabel => UnavailableReason ?? (IsInstalled ? "Installed · existing files" : "Available to download");
}

public sealed record SetupInspection(
    string Processor, string Graphics, string NeuralProcessor, long MemoryBytes,
    bool NpuDetected, bool RuntimeInstalled, string RuntimeStatus,
    string DriverStatus, IReadOnlyList<SetupModel> Models,
    long? RuntimeDownloadBytes, string DefaultModelStore, string? BlockingReason);

public sealed record SetupRequest(string ModelId, ExecutionMode Mode, string ModelStore, int ContextLength);
public sealed record SetupProgress(string Stage, string Message, long? CompletedBytes = null, long? TotalBytes = null)
{
    public double? Percent => TotalBytes is > 0 && CompletedBytes is { } done
        ? Math.Clamp(100d * done / TotalBytes.Value, 0, 100) : null;
}

public sealed record PreparedModel(SetupModel Model, ModelProfile Profile, RuntimeBroker Broker, string ManifestPath);
public sealed record RememberedSetup(string ModelId, string ManifestPath, int ContextLength, ExecutionMode Mode,
    string FlmPath, string ModelRoot, int SchemaVersion = 1);

public interface ISetupService
{
    Task<SetupInspection> InspectAsync(CancellationToken cancellationToken = default);
    /// <summary>Fetches the runtime and model files, then verifies them. Nothing loads, so a running model keeps running.</summary>
    Task DownloadAsync(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);
    /// <summary>Read the remembered descriptor without hardware probing, hashing model/runtime files or starting inference. Its broker is idle.</summary>
    Task<PreparedModel?> OpenRememberedAsync(CancellationToken cancellationToken = default);
    /// <summary>Verify an installed selection and return an idle broker. Missing files require explicit setup; nothing is downloaded.</summary>
    Task<PreparedModel> OpenInstalledAsync(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);
    /// <summary>Validate and activate the current descriptor. A failed activation can be retried without saving a new selection.</summary>
    Task ActivateAsync(PreparedModel prepared, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default);
    Task SaveAsync(PreparedModel prepared, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
