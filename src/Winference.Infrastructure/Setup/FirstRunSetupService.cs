using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Lemonade;
using Winference.Infrastructure.Windows;

namespace Winference.Infrastructure.Setup;

/// <summary>Coordinates explicit first-run discovery, acquisition and qualification.
/// Construction and inspection never start inference or write to model stores.</summary>
public sealed class FirstRunSetupService : ISetupService
{
    private readonly SetupPaths _paths;
    private readonly Func<CancellationToken, Task<SetupHardware>> _hardware;
    private readonly HttpClient _downloads;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _pullCleanup = new(1, 1);
    private CancellationTokenSource? _activeCancellation;
    private TaskCompletionSource? _activeCompletion;
    private OwnedProcessSession? _pull;
    private RuntimeBroker? _broker;
    private PreparedModel? _prepared;

    public FirstRunSetupService() : this(SetupPaths.CreateDefault()) { }
    public FirstRunSetupService(SetupPaths paths, Func<CancellationToken, Task<SetupHardware>>? inspectHardware = null, HttpClient? downloads = null)
    {
        _paths = paths;
        _hardware = inspectHardware ?? WindowsSetupHardware.InspectAsync;
        _downloads = downloads ?? new HttpClient(new HttpClientHandler { MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string SettingsPath => _paths.SettingsPath;
    public string DefaultModelStore => _paths.DefaultModelStore;

    public async Task<SetupInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var hardware = await _hardware(cancellationToken).ConfigureAwait(false);
            var catalog = FlmModelCatalog.Load(_paths.CatalogPath, _paths.ModelInfoPath);
            var runtime = await PinnedRuntimeAcquisition.FindTrustedRuntimeAsync(_paths, cancellationToken).ConfigureAwait(false);
            var modelRoots = _paths.ModelRoots.Concat([_paths.DefaultModelStore]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new(hardware.Processor, hardware.Graphics, hardware.NeuralProcessor, hardware.MemoryBytes,
                hardware.NpuDetected, runtime is not null,
                runtime is not null ? "AI component installed" : "AI component will be downloaded during setup",
                hardware.DriverStatus, catalog.Discover(modelRoots, hardware.NpuDetected, hardware.MemoryBytes),
                runtime is null ? PinnedRuntimeAcquisition.ArchiveBytes : 0, _paths.DefaultModelStore, hardware.BlockingReason);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new RuntimeException("setup_catalog", "Model information could not be read. Restore the complete Winference application folder and retry.", error);
        }
    }

    public Task DownloadAsync(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
        RunOperationAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(progress);
            if (request.Mode != ExecutionMode.NpuOnly)
                throw new RuntimeException("mode_unavailable", "Only NPU models download through this route.");
            progress.Report(new("Checking", "Checking the selected model and this computer…"));
            var inspection = await InspectAsync(token).ConfigureAwait(false);
            if (inspection.BlockingReason is not null) throw new RuntimeException("hardware_unavailable", inspection.BlockingReason);
            var candidate = inspection.Models.SingleOrDefault(m => m.Id == request.ModelId)
                ?? throw new RuntimeException("model_unknown", "Choose a model from the supported catalog.");
            if (!candidate.CanSelect) throw new RuntimeException("model_unavailable", candidate.UnavailableReason!);
            var catalog = FlmModelCatalog.Load(_paths.CatalogPath, _paths.ModelInfoPath);
            var model = catalog.Models.Single(m => m.Id == request.ModelId);
            var runtime = await PinnedRuntimeAcquisition.FindTrustedRuntimeAsync(_paths, token).ConfigureAwait(false)
                ?? await PinnedRuntimeAcquisition.AcquireAsync(_paths, progress, _downloads, token).ConfigureAwait(false);
            var modelRoot = candidate.ExistingModelRoot;
            if (modelRoot is null)
            {
                modelRoot = ValidateOwnedStore(request.ModelStore);
                await PullModelAsync(runtime, modelRoot, model, progress, token).ConfigureAwait(false);
            }
            progress.Report(new("CheckingFiles", "Verifying the selected model files…"));
            await FlmModelCatalog.VerifyFilesAsync(modelRoot, model, progress, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
}
