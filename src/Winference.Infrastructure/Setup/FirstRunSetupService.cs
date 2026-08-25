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

    public Task<PreparedModel?> OpenRememberedAsync(CancellationToken cancellationToken = default) =>
        RunOperationAsync(OpenRememberedCoreAsync, cancellationToken);

    public Task<PreparedModel> OpenInstalledAsync(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
        RunOperationAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(progress);
            if (request.Mode != ExecutionMode.NpuOnly)
                throw new RuntimeException("mode_unavailable", "This flow opens NPU models. Choose a GPU model file or a GPU + NPU folder from Models.");
            progress.Report(new("Checking", "Checking the installed model…"));
            var inspection = await InspectAsync(token).ConfigureAwait(false);
            if (inspection.BlockingReason is not null || !inspection.NpuDetected)
                throw new RuntimeException("hardware_unavailable", inspection.BlockingReason ?? "A supported AMD NPU is required by this build.");
            var candidate = inspection.Models.SingleOrDefault(model => model.Id == request.ModelId)
                ?? throw new RuntimeException("model_unknown", "Choose a model from the supported catalog.");
            if (!candidate.CanSelect) throw new RuntimeException("model_unavailable", candidate.UnavailableReason!);
            if (!candidate.IsInstalled || candidate.ExistingModelRoot is null)
                throw new RuntimeException("missing_model", "This model needs files. Review its setup requirements before downloading anything.");
            if (!inspection.RuntimeInstalled)
                throw new RuntimeException("missing_runtime", "An AI component is missing. Review setup requirements before downloading it.");
            if (request.ContextLength < 256 || request.ContextLength > candidate.MaximumContextLength)
                throw new RuntimeException("context_invalid", "Choose a context length supported by the selected model.");
            var runtime = await PinnedRuntimeAcquisition.FindTrustedRuntimeAsync(_paths, token).ConfigureAwait(false)
                ?? throw new RuntimeException("runtime_mismatch", "The AI component changed. Review setup requirements before continuing.");
            var model = FlmModelCatalog.Load(_paths.CatalogPath, _paths.ModelInfoPath).Models.Single(model => model.Id == request.ModelId);
            var files = await FlmModelCatalog.VerifyFilesAsync(candidate.ExistingModelRoot, model, progress, token).ConfigureAwait(false);
            var manifest = await WriteManifestAsync(model, runtime, candidate.ExistingModelRoot, files, request.ContextLength, token).ConfigureAwait(false);
            return await OpenDescriptorAsync(candidate, manifest, runtime, candidate.ExistingModelRoot).ConfigureAwait(false);
        }, cancellationToken);

    public Task ActivateAsync(PreparedModel prepared, IProgress<SetupProgress> progress, CancellationToken cancellationToken = default) =>
        RunOperationAsync(async token =>
        {
            await ActivateCoreAsync(prepared, progress, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task<PreparedModel?> OpenRememberedCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(_paths.SettingsPath)) return null;
        RememberedSetup? saved;
        try { saved = await AtomicJson.ReadAsync<RememberedSetup>(_paths.SettingsPath, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or JsonException)
        { throw new RuntimeException("setup_state", "The saved model selection could not be read. Choose your model again; existing files were preserved.", error); }
        try
        {
            if (saved is null || saved.SchemaVersion != 1 || saved.Mode != ExecutionMode.NpuOnly ||
                string.IsNullOrWhiteSpace(saved.ManifestPath) || string.IsNullOrWhiteSpace(saved.FlmPath) || string.IsNullOrWhiteSpace(saved.ModelRoot) ||
                !Path.IsPathFullyQualified(saved.ManifestPath) || !Path.IsPathFullyQualified(saved.FlmPath) || !Path.IsPathFullyQualified(saved.ModelRoot) ||
                !Path.GetFullPath(saved.ManifestPath).StartsWith(Path.GetFullPath(Path.Combine(_paths.DataRoot, "Manifests")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new RuntimeException("setup_state", "The saved setup is not compatible with this build. Choose your model again.");
            if (!File.Exists(saved.ManifestPath))
                throw new RuntimeException("setup_missing", "The saved model information is missing. Choose your model again; existing files were preserved.");
            var catalog = FlmModelCatalog.Load(_paths.CatalogPath, _paths.ModelInfoPath);
            var model = catalog.Models.SingleOrDefault(m => m.Id == saved.ModelId)
                ?? throw new RuntimeException("model_unknown", "The saved model is no longer in this build's supported catalog. Choose another model.");
            if (saved.ContextLength < 256 || saved.ContextLength > model.ContextLength)
                throw new RuntimeException("context_invalid", "The saved context is not supported by the selected model. Choose your model again.");
            using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(saved.ManifestPath, token).ConfigureAwait(false)))
            {
                var root = manifest.RootElement;
                var flmHash = root.GetProperty("flmExecutableSha256").GetString();
                if (root.GetProperty("checkpoint").GetString() != saved.ModelId ||
                    root.GetProperty("modelFamilyId").GetString() != saved.ModelId ||
                    root.GetProperty("providerModelId").GetString() != model.ProviderId ||
                    root.GetProperty("modelDirectory").GetString() != model.DirectoryName ||
                    root.GetProperty("contextLength").GetInt32() != saved.ContextLength ||
                    root.GetProperty("setupRuntimePath").GetString() != saved.FlmPath ||
                    root.GetProperty("setupModelRoot").GetString() != saved.ModelRoot ||
                    root.GetProperty("lemonadeExecutableSha256").GetString() != PinnedRuntimeAcquisition.LemonadeExeSha256 ||
                    root.GetProperty("lemonadeVersion").GetString() != "2026.42.1" ||
                    flmHash is not (PinnedRuntimeAcquisition.InstalledExeSha256 or PinnedRuntimeAcquisition.PortableExeSha256))
                    throw new RuntimeException("setup_identity", "The saved model selection and its verified files do not match. Choose your model again.");
                var files = root.GetProperty("modelFiles").Deserialize<VerifiedModelFile[]>(AtomicJson.Options);
                if (files is null || files.Length != model.Files.Count || files.Any(file => file is null) ||
                    !files.Select(file => file.Name).SequenceEqual(model.Files.Select(file => file.Name)) ||
                    files.Where((file, index) => file.LengthBytes != model.Files[index].LengthBytes ||
                        file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit) ||
                        model.Files[index].Sha256 is { } supplierHash && file.Sha256 != supplierHash).Any() ||
                    root.GetProperty("artifactId").GetString() != "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(files, AtomicJson.Options))))
                    throw new RuntimeException("setup_identity", "The saved model file list does not match its verified artifact. Choose your model again.");
            }
            // Metadata only: do not enumerate/hash model files or invoke hardware/runtime checks here.
            var candidate = new SetupModel(model.Id, model.DisplayName, model.Publisher, model.Description,
                0, model.StorageBytes, "Installed", saved.ModelRoot, model.UnavailableReason,
                model.ContextLength, saved.ContextLength, FlmModelCatalog.Revision);
            return await OpenDescriptorAsync(candidate, saved.ManifestPath, saved.FlmPath, saved.ModelRoot).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new RuntimeException("setup_state", "The saved model information could not be read. Choose your model again; existing files were preserved.", error); }
    }

    private async Task ActivateCoreAsync(PreparedModel prepared, IProgress<SetupProgress> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_sync)
        {
            if (!ReferenceEquals(prepared, _prepared) || !ReferenceEquals(prepared.Broker, _broker))
                throw new RuntimeException("setup_identity", "The selected model changed. Choose it again before loading.");
        }
        progress.Report(new("Checking", $"Checking {prepared.Model.DisplayName} before loading…"));
        var hardware = await _hardware(token).ConfigureAwait(false);
        if (!hardware.NpuDetected || hardware.BlockingReason is not null)
            throw new RuntimeException("hardware_unavailable", hardware.BlockingReason ?? "A supported AMD NPU is required by this build.");
        var catalog = FlmModelCatalog.Load(_paths.CatalogPath, _paths.ModelInfoPath);
        var candidate = catalog.Discover([], true, hardware.MemoryBytes).SingleOrDefault(m => m.Id == prepared.Model.Id)
            ?? throw new RuntimeException("model_unknown", "The selected model is no longer available in this build. Choose another model.");
        if (!candidate.CanSelect) throw new RuntimeException("model_unavailable", candidate.UnavailableReason!);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(prepared.ManifestPath, token).ConfigureAwait(false));
        var runtime = manifest.RootElement.GetProperty("setupRuntimePath").GetString()!;
        var trustedPaths = _paths with { RuntimeCandidates = [runtime] };
        var trusted = await PinnedRuntimeAcquisition.FindTrustedRuntimeAsync(trustedPaths, token).ConfigureAwait(false);
        if (trusted is null || !string.Equals(Path.GetFullPath(trusted), Path.GetFullPath(runtime), StringComparison.OrdinalIgnoreCase))
            throw new RuntimeException("runtime_mismatch", "The saved AI component is missing or changed. Choose a model to review its setup requirements.");
        await ValidateNpuAsync(runtime, progress, token).ConfigureAwait(false);
        progress.Report(new("Preparing", $"Loading {prepared.Model.DisplayName} on the NPU…"));
        // The existing backend rechecks the complete model/runtime hashes and proves the owned NPU route.
        await prepared.Broker.StartAsync(prepared.Profile, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    public async Task SaveAsync(PreparedModel prepared, CancellationToken cancellationToken = default)
    {
        if (_activeCompletion is not null || !ReferenceEquals(prepared, _prepared) || prepared.Broker.Snapshot is not { State: RuntimeState.Ready, Session: { } session } ||
            session.Profile != prepared.Profile || !File.Exists(prepared.ManifestPath))
            throw new RuntimeException("setup_unverified", "The selected model has not finished verification. Setup was not saved.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(prepared.ManifestPath, cancellationToken).ConfigureAwait(false));
        var configuration = document.RootElement;
        var runtime = configuration.GetProperty("setupRuntimePath").GetString()!;
        var modelRoot = configuration.GetProperty("setupModelRoot").GetString()!;
        await AtomicJson.WriteAsync(_paths.SettingsPath, new RememberedSetup(prepared.Model.Id, prepared.ManifestPath,
            prepared.Profile.ContextLength, prepared.Profile.ExecutionMode, runtime, modelRoot), cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cancellation;
        Task? operation;
        OwnedProcessSession? pull;
        RuntimeBroker? broker;
        lock (_sync) { cancellation = _activeCancellation; operation = _activeCompletion?.Task; pull = _pull; broker = _broker; }
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        if (pull is not null) await StopPullAsync(pull).ConfigureAwait(false);
        if (broker is not null) await broker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        lock (_sync) { pull = _pull; broker = _broker; }
        if (pull is not null) await StopPullAsync(pull).ConfigureAwait(false);
        if (broker is not null) await broker.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
