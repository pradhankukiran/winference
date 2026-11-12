using Winference.Core.Discover;
using Winference.Core.Downloads;
using Winference.Core.Gpu;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Setup;
using Winference.Infrastructure.Discover;
using Winference.Infrastructure.Gpu;
using Winference.Infrastructure.Hybrid;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Models;

/// <summary>
/// Read-only aggregation of the approved GPU catalog, the pinned FLM catalog,
/// registered GGUF files and registered hybrid folders. Construction and
/// inspection never download, load, move or rewrite a model.
/// </summary>
public sealed class ModelLibraryService
{
    private static readonly Dictionary<string, ModelFamily> KnownFamilies = new(StringComparer.Ordinal)
    {
        ["llama-3.2-1b-instruct"] = new("llama-3.2-1b-instruct", "Llama 3.2 1B Instruct", "Meta",
            "Meta's smallest Llama 3.2 instruct model. GPU, GPU + NPU, and NPU builds are separate artifacts."),
        ["llama-3.1-8b-instruct"] = new("llama-3.1-8b-instruct", "Llama 3.1 8B Instruct", "Meta",
            "Meta's 8B long-context instruct model. The GPU build needs a larger memory budget than the 1B model."),
        ["qwen2.5-3b-instruct"] = new("qwen2.5-3b-instruct", "Qwen 2.5 3B Instruct", "Alibaba Qwen",
            "A compact general-purpose instruct model. GPU and NPU builds are separate artifacts."),
        ["qwen2.5-0.5b-instruct"] = new("qwen2.5-0.5b-instruct", "Qwen 2.5 0.5B Instruct", "Alibaba Qwen",
            "A very small instruct model. AMD's GPU + NPU 0.5B export is visible with an answer accuracy warning."),
        ["phi-4-mini-instruct"] = new("phi-4-mini-instruct", "Phi-4 Mini Instruct", "Microsoft",
            "Microsoft's compact instruct model. The tested NPU package and the GPU GGUF mapping are different artifacts."),
        ["deepseek-r1-distill-llama-8b"] = new("deepseek-r1-distill-llama-8b", "DeepSeek R1 Distill Llama 8B", "DeepSeek",
            "A reasoning model listed by FastFlowLM. No GPU or GPU + NPU build is approved in this build.")
    };

    private readonly ISetupService _setup;
    private readonly GpuModelService _gpuModels;
    private readonly HybridModelService _hybridModels;
    private readonly GpuModelDownloadService _downloads;
    private readonly ModelRemovalService _removal;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly SetupPaths _paths;
    private readonly CatalogRefreshService? _catalogs;
    private readonly HuggingFaceClient _huggingFace;
    private readonly HybridModelDownloadService _hybridDownloads;
    private IReadOnlyList<RegistryModel> _registry = [];

    public ModelLibraryService(SetupPaths paths, ISetupService setup, GpuModelService gpuModels,
        HybridModelService hybridModels, HttpClient? http = null, string? gpuCatalogPath = null,
        CatalogRefreshService? catalogs = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _catalogs = catalogs;
        _huggingFace = new HuggingFaceClient(http);
        _hybridDownloads = new HybridModelDownloadService(paths.DataRoot, hybridModels, _huggingFace, http);
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _gpuModels = gpuModels ?? throw new ArgumentNullException(nameof(gpuModels));
        _hybridModels = hybridModels ?? throw new ArgumentNullException(nameof(hybridModels));
        GpuCatalog = GpuModelCatalog.Load(gpuCatalogPath ?? Path.Combine(AppContext.BaseDirectory, "Catalogs", "gpu-approved.json"));
        _downloads = new GpuModelDownloadService(paths.DataRoot, gpuModels, http);
        _removal = new ModelRemovalService(paths, gpuModels, GpuCatalog);
    }

    public GpuModelCatalog GpuCatalog { get; }
    public DownloadCapabilities DownloadCapabilities => GpuModelDownloadService.Capabilities;
    public string DestinationFor(GpuCatalogModel model) => _downloads.DestinationFor(model);

    public async Task<GpuModelRegistration> DownloadGpuAsync(GpuCatalogModel model,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _downloads.DownloadAsync(model, progress, cancellationToken).ConfigureAwait(false); }
        finally { _mutations.Release(); }
    }

    /// <summary>The tested GGUF entry behind a library profile, when it came from Lemonade's list.</summary>
    public RegistryModel? RegistryFor(LibraryProfile profile) =>
        profile.Artifact.Id.StartsWith(RegistryArtifactPrefix, StringComparison.Ordinal)
            ? _registry.FirstOrDefault(model => RegistryId(model) == profile.Artifact.Id[RegistryArtifactPrefix.Length..]) : null;

    /// <summary>Pins the exact file, version and fingerprint on Hugging Face so the download can be verified.</summary>
    public async Task<GpuCatalogModel> ResolveRegistryAsync(RegistryModel model, CancellationToken cancellationToken = default)
    {
        var file = await _huggingFace.ResolveGgufAsync(model.Repository, model.FileName, model.Quantization, cancellationToken)
            .ConfigureAwait(false);
        var resolved = new GpuCatalogModel(RegistryId(model), RegistryFamily(model), file.FileName, file.Download, file.SourcePage,
            file.Revision, file.Sha256, file.LengthBytes, model.Quantization ?? QuantizationOf(file.FileName), 4096);
        resolved.Validate();
        return resolved;
    }

    public HuggingFaceClient HuggingFace => _huggingFace;

    /// <summary>Downloads and registers a tested GPU + NPU export from AMD's Hugging Face models.</summary>
    public async Task<HybridModelRegistration> DownloadHybridAsync(RegistryModel model, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (model.Mode != ExecutionMode.GpuNpu) throw new ArgumentException("Only GPU + NPU exports download here.", nameof(model));
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _hybridDownloads.DownloadAsync(model.Repository, progress, cancellationToken).ConfigureAwait(false); }
        finally { _mutations.Release(); }
    }

    /// <summary>A community GGUF chosen from search, as a pinned, verifiable download.</summary>
    public GpuCatalogModel CommunityModel(HuggingFaceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var quantization = QuantizationOf(file.FileName);
        var id = "hf-" + Slug(file.Repository.Replace('/', '-') + "-" + quantization);
        if (id.Length > 80) id = id[..80].TrimEnd('-');
        var name = file.Repository[(file.Repository.IndexOf('/') + 1)..];
        if (name.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        var owner = file.Repository[..file.Repository.IndexOf('/')];
        var family = new ModelFamily("community-" + ModelFamilyKey.For(name), name, ModelMakers.For(name) ?? owner,
            $"A community upload on Hugging Face by {owner}. Winference hasn't tested it; it's verified against Hugging Face's fingerprint when downloaded.");
        var model = new GpuCatalogModel(id, family, file.FileName, file.Download, file.SourcePage, file.Revision, file.Sha256,
            file.LengthBytes, quantization.Length <= 40 ? quantization : "GGUF", 4096);
        model.Validate();
        return model;
    }

    public bool CanRemove(LibraryProfile profile) => _removal.CanRemove(profile);

    public async Task<ModelRemovalPlan> PlanRemovalAsync(LibraryProfile profile, CancellationToken token = default)
    {
        await _mutations.WaitAsync(token).ConfigureAwait(false);
        try { return await _removal.PlanAsync(profile, token).ConfigureAwait(false); }
        finally { _mutations.Release(); }
    }

    public async Task<ModelRemovalResult> RemoveAsync(ModelRemovalPlan plan, CancellationToken token = default)
    {
        await _mutations.WaitAsync(token).ConfigureAwait(false);
        try { return await _removal.RemoveAsync(plan, token).ConfigureAwait(false); }
        finally { _mutations.Release(); }
    }

    public Task<ModelLibrarySnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
        InspectCoreAsync(null, cancellationToken);

    /// <summary>Reuse an inspection the caller already performed instead of probing hardware twice.</summary>
    public Task<ModelLibrarySnapshot> InspectAsync(SetupInspection inspection, CancellationToken cancellationToken = default) =>
        InspectCoreAsync(inspection ?? throw new ArgumentNullException(nameof(inspection)), cancellationToken);

    private async Task<ModelLibrarySnapshot> InspectCoreAsync(SetupInspection? provided, CancellationToken cancellationToken)
    {
        var families = new Dictionary<string, FamilyBuilder>(StringComparer.Ordinal);
        var notices = new List<string>();
        var inspection = provided;
        IReadOnlyList<GpuModelRegistration> gpuModels = [];
        IReadOnlyList<HybridModelRegistration> hybridModels = [];

        if (inspection is null)
        {
            try { inspection = await _setup.InspectAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { notices.Add(Message(error, "NPU model information could not be checked.")); }
        }

        try { gpuModels = await _gpuModels.ReadRegisteredAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { notices.Add(Message(error, "Registered GPU models could not be checked.")); }

        try { hybridModels = await _hybridModels.ReadRegisteredAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { notices.Add(Message(error, "Registered GPU + NPU models could not be checked.")); }

        _registry = LoadRegistry(notices);
        AddGpuCatalog(families, gpuModels);
        AddRegistryGguf(families, gpuModels);
        AddRegisteredGpu(families, gpuModels);
        if (inspection is not null) AddFlm(families, inspection, FlmCapabilities());
        AddHybrid(families, hybridModels, inspection);
        AddRegistryHybrid(families, hybridModels, inspection);
        AddHybridImport(families);

        var summary = inspection is null
            ? "Hardware details unavailable."
            : string.Join(" · ", new[]
            {
                inspection.Processor,
                inspection.MemoryBytes > 0 ? $"{inspection.MemoryBytes / 1_073_741_824d:0.#} GB memory" : "",
                inspection.NpuDetected ? "AMD NPU detected" : "AMD NPU not detected"
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var ordered = families.Values
            .Select(builder => new LibraryFamily(builder.Family, builder.Profiles
                .OrderBy(profile => profile.ExecutionMode)
                .ThenBy(profile => profile.Compatibility.State)
                .ThenBy(profile => profile.ProfileId, StringComparer.Ordinal)
                .ToArray()))
            .OrderBy(family => family.Family.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var family in ordered) family.Validate();
        return new(ordered, summary, notices.Count == 0 ? null : string.Join(" ", notices.Distinct(StringComparer.Ordinal)));
    }

    private void AddGpuCatalog(Dictionary<string, FamilyBuilder> families,
        IReadOnlyList<GpuModelRegistration> registrations)
    {
        foreach (var entry in GpuCatalog.Models)
        {
            var registered = registrations.FirstOrDefault(model => model.Sha256 == entry.Sha256);
            var usable = registered is not null && IsUsable(registered);
            var destination = _downloads.DestinationFor(entry);
            var downloaded = File.Exists(destination) && new FileInfo(destination).Length == entry.LengthBytes;
            var partial = !downloaded && File.Exists(destination + ".part") ? new FileInfo(destination + ".part").Length : 0;
            var state = usable ? ModelAvailability.Installed : ModelAvailability.DownloadRequired;
            var summary = usable ? "Verified GGUF is registered for GPU use."
                : registered is not null ? "A registration points to a missing or changed file. Download the approved file again."
                : downloaded ? "An approved file is already downloaded. Verify and add it."
                : partial > 0 ? $"A partial download is present ({partial / 1_000_000_000d:0.00} GB of {entry.LengthBytes / 1_000_000_000d:0.00} GB). Resume to finish it."
                : "Approved GPU download. No model files are present.";
            var artifact = entry.Artifact;
            var profile = new LibraryProfile(
                registered?.Profile.ProfileId ?? entry.ProfileId, ExecutionMode.GpuOnly,
                usable ? "Vulkan llama.cpp + ODS bridge" : "Vulkan llama.cpp",
                artifact, new(state, summary),
                usable ? 0 : entry.LengthBytes, entry.LengthBytes,
                usable ? registered!.ModelPath : downloaded ? destination : null,
                usable, !usable, false,
                usable
                    ? "Registered GGUF: header, full-file SHA-256 and file identity verified. GPU device qualification happens when it loads."
                    : registered is not null
                        ? "Registration metadata exists, but the file is missing or its size changed. Nothing was moved or deleted. Download again or choose the file."
                        : "Approved mapping from the attributed ODS catalog. The file hash is pinned; execution is not yet qualified on this PC.");
            Get(families, entry.Family).Add(profile);
        }
    }

    private void AddRegisteredGpu(Dictionary<string, FamilyBuilder> families,
        IReadOnlyList<GpuModelRegistration> registrations)
    {
        foreach (var model in registrations)
        {
            if (GpuCatalog.Models.Any(entry => entry.Sha256 == model.Sha256)) continue;
            var registry = _registry.FirstOrDefault(entry => IsInFolder(model.ModelPath, RegistryFolder(entry)));
            var family = registry is not null ? RegistryFamily(registry) : FamilyForLocalGguf(model);
            var usable = IsUsable(model);
            var profile = new LibraryProfile(
                model.Profile.ProfileId, ExecutionMode.GpuOnly, "Vulkan llama.cpp + ODS bridge",
                new("sha256:" + model.Sha256, family, ModelArtifactFormat.Gguf,
                    model.Metadata.Quantization, "User-selected GGUF", "sha256:" + model.Sha256,
                    model.LengthBytes, model.Sha256, "Vulkan llama.cpp"),
                usable
                    ? new(ModelAvailability.Installed, "Verified GGUF is registered for GPU use.")
                    : new(ModelAvailability.Unavailable,
                        "The registered GGUF file is missing or its size changed. It was not moved or deleted.",
                        "Choose the GGUF file again to repair this registration; its file is missing or changed. Your chats were kept."),
                0, model.LengthBytes, model.ModelPath, usable, false, false,
                usable
                    ? $"Registered GGUF: {model.Metadata.Architecture}, {model.Metadata.Quantization}, SHA-256 verified in place."
                    : "Registration metadata exists, but its file is missing or changed. No model file was modified.",
                registry?.Capabilities ?? ModelCapabilities.None, registry is null ? CatalogTier.Community : CatalogTier.Tested);
            Get(families, family).Add(profile);
        }
    }

    private IReadOnlyList<RegistryModel> LoadRegistry(List<string> notices)
    {
        var path = _catalogs?.PathFor(CatalogRefreshService.LemonadeModels)
            ?? Path.Combine(AppContext.BaseDirectory, "Runtimes", "Lemonade", "resources", "server_models.json");
        try { return File.Exists(path) ? LemonadeRegistry.Load(path) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            notices.Add("The tested GPU model list could not be read.");
            return [];
        }
    }

    private Dictionary<string, ModelCapabilities> FlmCapabilities()
    {
        try
        {
            return FlmModelCatalog.Load(_paths.CatalogPath, _paths.ModelInfoPath).Models
                .ToDictionary(model => model.Id, model => ModelCapabilityLabels.FromLabels(model.Labels), StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Winference.Core.Runtime.RuntimeException)
        { return new(StringComparer.Ordinal); }
    }

    /// <summary>Lemonade's tested GGUF list. A download is pinned to an exact file only when Get is chosen.</summary>
    private void AddRegistryGguf(Dictionary<string, FamilyBuilder> families, IReadOnlyList<GpuModelRegistration> registrations)
    {
        foreach (var model in _registry.Where(entry => entry.Mode == ExecutionMode.GpuOnly))
        {
            // Already approved with a pinned hash, or already downloaded and shown as a registered file.
            if (GpuCatalog.Models.Any(approved => approved.SourcePage.AbsolutePath.Trim('/') == model.Repository &&
                (approved.FileName == model.FileName || model.Quantization is { } quant && HuggingFaceClient.MatchesQuantization(approved.FileName, quant))))
                continue;
            var folder = RegistryFolder(model);
            if (registrations.Any(registered => IsInFolder(registered.ModelPath, folder))) continue;
            var partial = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.part").Any();
            var family = RegistryFamily(model);
            var quantization = model.Quantization ?? QuantizationOf(model.FileName!);
            var profile = new LibraryProfile(
                "tested-gguf-" + RegistryId(model), ExecutionMode.GpuOnly, "Vulkan llama.cpp",
                new(RegistryArtifactPrefix + RegistryId(model), family, ModelArtifactFormat.Gguf, quantization,
                    model.SourcePage, "latest", model.SizeBytes, null, "Vulkan llama.cpp"),
                new(ModelAvailability.DownloadRequired, partial
                    ? "A partial download is present. Get resumes it."
                    : "Tested by Lemonade with llama.cpp. Get pins and verifies the exact file."),
                model.SizeBytes, model.SizeBytes, null, false, true, false,
                $"From Lemonade's tested model list. Source: {model.SourcePage}." +
                    (model.HasVisionProjector ? " Pictures aren't supported for GPU models yet, so it works as a text model." : string.Empty),
                model.Capabilities & ~ModelCapabilities.Vision, CatalogTier.Tested);
            Get(families, family).Add(profile);
        }
    }

    /// <summary>AMD's GPU + NPU exports from Lemonade's tested list, downloaded whole when Get is chosen.</summary>
    private void AddRegistryHybrid(Dictionary<string, FamilyBuilder> families, IReadOnlyList<HybridModelRegistration> registrations,
        SetupInspection? inspection)
    {
        foreach (var model in _registry.Where(entry => entry.Mode == ExecutionMode.GpuNpu))
        {
            var folder = _hybridDownloads.FolderFor(model.Repository);
            if (registrations.Any(registered => string.Equals(registered.RepositoryId, model.Repository, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(registered.ModelDirectory), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)))
                continue;
            var reason = inspection is { NpuDetected: false } ? "A supported AMD NPU is required for GPU + NPU models." : null;
            var name = model.Name.EndsWith("-Hybrid", StringComparison.Ordinal) ? model.Name[..^7] : model.Name;
            var family = new ModelFamily("tested-" + ModelFamilyKey.For(model.Name), name, ModelMakers.For(name) ?? "AMD",
                "Tested by Lemonade on AMD Ryzen AI. Exported for GPU + NPU by AMD.");
            var profile = new LibraryProfile(
                "tested-hybrid-" + RegistryId(model), ExecutionMode.GpuNpu, "AMD Ryzen AI 1.7 hybrid worker",
                new(RegistryArtifactPrefix + RegistryId(model), family, ModelArtifactFormat.RyzenAiHybrid, "1.7",
                    model.SourcePage, "latest", model.SizeBytes, null, "AMD Ryzen AI 1.7 hybrid worker"),
                reason is null
                    ? new(ModelAvailability.DownloadRequired, Directory.Exists(folder)
                        ? "Part of this model is already downloaded. Get finishes it."
                        : "Tested by Lemonade. Get downloads AMD's export and verifies every file.")
                    : new(ModelAvailability.Unavailable, reason, reason),
                model.SizeBytes, model.SizeBytes, null, false, reason is null, false,
                $"AMD Ryzen AI 1.7 GPU + NPU export from Lemonade's tested list. Source: {model.SourcePage}.",
                model.Capabilities & ~ModelCapabilities.Vision, CatalogTier.Tested);
            Get(families, family).Add(profile);
        }
    }

    private const string RegistryArtifactPrefix = "registry:";

    private static string RegistryId(RegistryModel model)
    {
        var id = "lemonade-" + Slug(model.Name);
        return id.Length <= 80 ? id : id[..80].TrimEnd('-');
    }

    private string RegistryFolder(RegistryModel model) => Path.Combine(_paths.DataRoot, "Models", "Gpu", RegistryId(model));

    private static bool IsInFolder(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);

    private static ModelFamily RegistryFamily(RegistryModel model)
    {
        var name = model.Name.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase) ? model.Name[..^5] : model.Name;
        var publisher = model.Repository[..model.Repository.IndexOf('/')];
        var family = new ModelFamily("tested-" + ModelFamilyKey.For(model.Name), name, publisher,
            $"Tested by Lemonade with llama.cpp. Published on Hugging Face by {publisher}.");
        family.Validate();
        return family;
    }

    private static string QuantizationOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var match = System.Text.RegularExpressions.Regex.Match(stem, @"(?i)(?:UD-)?(?:I?Q\d[A-Z0-9_]*|BF16|F16|F32)$");
        return match.Success ? match.Value : "GGUF";
    }

    private static void AddFlm(Dictionary<string, FamilyBuilder> families, SetupInspection inspection,
        IReadOnlyDictionary<string, ModelCapabilities> capabilities)
    {
        foreach (var model in inspection.Models)
        {
            var familyId = FlmFamilyId(model.Id);
            var family = KnownFamily(familyId) ?? new ModelFamily(familyId, model.DisplayName,
                model.Publisher, model.Description);
            var installed = model.IsInstalled;
            var unavailable = model.UnavailableReason;
            var state = unavailable is not null ? ModelAvailability.Unavailable
                : installed ? ModelAvailability.Installed
                : ModelAvailability.DownloadRequired;
            var summary = state switch
            {
                ModelAvailability.Installed => "Installed NPU package. Existing files are reused in place.",
                ModelAvailability.DownloadRequired when model.InstallationState == "Incomplete" =>
                    "Incomplete NPU files found. The pinned FastFlowLM download can resume.",
                ModelAvailability.DownloadRequired => "Available through the pinned FastFlowLM download.",
                _ => unavailable ?? "This NPU package is unavailable."
            };
            var profile = new LibraryProfile(
                $"flm-{model.Id.Replace(':', '-')}-ctx{model.SuggestedContextLength}", ExecutionMode.NpuOnly,
                $"Lemonade {PinnedRuntimeAcquisition.LemonadeVersion} + FastFlowLM {PinnedRuntimeAcquisition.FlmVersion}",
                new("flm:" + model.Id, family, ModelArtifactFormat.FastFlowLm, "FastFlowLM package",
                    $"FastFlowLM {PinnedRuntimeAcquisition.FlmVersion} catalog", model.CatalogRevision, model.StorageBytes, null,
                    $"Lemonade + FastFlowLM {PinnedRuntimeAcquisition.FlmVersion}"),
                new(state, summary, unavailable),
                installed ? 0 : model.DownloadBytes, model.StorageBytes, model.ExistingModelRoot,
                state == ModelAvailability.Installed, state == ModelAvailability.DownloadRequired, false,
                "FastFlowLM catalog entry with supplier file sizes. Local NPU answer quality is not implied.",
                capabilities.GetValueOrDefault(model.Id));
            Get(families, family).Add(profile);
        }
    }

    private static void AddHybrid(Dictionary<string, FamilyBuilder> families,
        IReadOnlyList<HybridModelRegistration> registrations, SetupInspection? inspection)
    {
        foreach (var model in registrations)
        {
            var family = FamilyForHybrid(model);
            var canExecute = HybridModelService.CanExecute(model);
            var reason = !canExecute ? HybridModelService.ExecutionStatus(model)
                : inspection is null ? "AMD NPU support could not be checked."
                : !inspection.NpuDetected ? "A supported AMD NPU is required for GPU + NPU execution."
                : null;
            var state = reason is null ? ModelAvailability.Installed : ModelAvailability.Unavailable;
            var profile = new LibraryProfile(
                model.Profile.ProfileId, ExecutionMode.GpuNpu, "AMD Ryzen AI 1.7 hybrid worker",
                new("sha256:" + model.Sha256, family, ModelArtifactFormat.RyzenAiHybrid, "1.7",
                    model.RepositoryId is { Length: > 0 } repository ? repository : model.ModelDirectory,
                    model.Revision is { Length: > 0 } revision ? revision : "sha256:" + model.Sha256,
                    model.LengthBytes, model.Sha256, "AMD Ryzen AI 1.7 hybrid worker"),
                new(state, reason ?? "Verified GPU + NPU files are registered and reused in place.", reason),
                0, model.LengthBytes, model.ModelDirectory, state == ModelAvailability.Installed, false, false,
                reason ?? "Registered GPU + NPU export: every top-level file hashed, genai_config.json validated. Device readiness is checked when it loads.");
            Get(families, family).Add(profile);
        }
    }

    private static void AddHybridImport(Dictionary<string, FamilyBuilder> families)
    {
        var family = new ModelFamily("amd-ryzen-ai-hybrid", "AMD Ryzen AI GPU + NPU export", "AMD",
            "A completed AMD Ryzen AI GPU + NPU export. Any compatible export folder can be imported; the app does not download gated exports.");
        Get(families, family).Add(new LibraryProfile(
            "hybrid-import", ExecutionMode.GpuNpu, "AMD Ryzen AI 1.7 hybrid worker",
            new("hybrid:import", family, ModelArtifactFormat.RyzenAiHybrid, "Export-defined", "AMD Ryzen AI export folder",
                "user-selected", null, null, "AMD Ryzen AI 1.7 hybrid worker"),
            new(ModelAvailability.ImportRequired, "Choose a completed AMD Ryzen AI export folder."),
            null, null, null, false, false, true,
            "Folder import: every top-level file is hashed in place and genai_config.json is validated. No gated export is downloaded by the app."));
    }

    private static FamilyBuilder Get(Dictionary<string, FamilyBuilder> families, ModelFamily family)
    {
        if (families.TryGetValue(family.Id, out var existing)) return existing;
        var builder = new FamilyBuilder(family);
        families.Add(family.Id, builder);
        return builder;
    }

    private static ModelFamily? KnownFamily(string id) => KnownFamilies.GetValueOrDefault(id);

    private static string FlmFamilyId(string checkpoint) => checkpoint switch
    {
        "phi4-mini-it:4b" => "phi-4-mini-instruct",
        "llama3.2:1b" => "llama-3.2-1b-instruct",
        "llama3.1:8b" => "llama-3.1-8b-instruct",
        "qwen2.5-it:3b" => "qwen2.5-3b-instruct",
        "deepseek-r1:8b" => "deepseek-r1-distill-llama-8b",
        _ => "flm-" + Slug(checkpoint.Replace(':', '-'))
    };

    private static ModelFamily FamilyForHybrid(HybridModelRegistration model)
    {
        var identity = ((model.RepositoryId ?? "") + " " + model.DisplayName + " " + model.ModelDirectory).ToLowerInvariant();
        if (identity.Contains("llama-3.2-1b", StringComparison.Ordinal) || identity.Contains("llama3.2-1b", StringComparison.Ordinal))
            return KnownFamily("llama-3.2-1b-instruct")!;
        if (identity.Contains("qwen2.5-0.5b", StringComparison.Ordinal))
            return KnownFamily("qwen2.5-0.5b-instruct")!;
        if (identity.Contains("qwen2.5-3b", StringComparison.Ordinal))
            return KnownFamily("qwen2.5-3b-instruct")!;
        if (identity.Contains("llama-3.1-8b", StringComparison.Ordinal) || identity.Contains("llama3.1-8b", StringComparison.Ordinal))
            return KnownFamily("llama-3.1-8b-instruct")!;
        if (identity.Contains("phi-4-mini", StringComparison.Ordinal) || identity.Contains("phi4-mini", StringComparison.Ordinal))
            return KnownFamily("phi-4-mini-instruct")!;
        var fallbackId = "local-" + Slug(model.DisplayName);
        return KnownFamily(fallbackId) ?? new ModelFamily(fallbackId, model.DisplayName,
            "Local AMD GPU + NPU export", "A user-provided AMD Ryzen AI GPU + NPU export.");
    }

    private static ModelFamily FamilyForLocalGguf(GpuModelRegistration model)
    {
        var identity = (model.DisplayName + " " + Path.GetFileName(model.ModelPath)).ToLowerInvariant();
        if (identity.Contains("llama-3.2-1b", StringComparison.Ordinal) || identity.Contains("llama3.2-1b", StringComparison.Ordinal))
            return KnownFamily("llama-3.2-1b-instruct")!;
        if (identity.Contains("llama-3.1-8b", StringComparison.Ordinal) || identity.Contains("llama3.1-8b", StringComparison.Ordinal))
            return KnownFamily("llama-3.1-8b-instruct")!;
        if (identity.Contains("qwen2.5-3b", StringComparison.Ordinal))
            return KnownFamily("qwen2.5-3b-instruct")!;
        if (identity.Contains("qwen2.5-0.5b", StringComparison.Ordinal))
            return KnownFamily("qwen2.5-0.5b-instruct")!;
        if (identity.Contains("phi-4-mini", StringComparison.Ordinal) || identity.Contains("phi4-mini", StringComparison.Ordinal))
            return KnownFamily("phi-4-mini-instruct")!;
        return new ModelFamily("local-gguf-" + model.Sha256[..12], model.DisplayName, "Local GGUF",
            $"{model.Metadata.Architecture} GGUF registered from a user-selected file.");
    }

    private static bool IsUsable(GpuModelRegistration model) => GpuModelService.IsFilePresent(model);

    private static string Slug(string value)
    {
        var characters = value.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = new string(characters);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length == 0) return "model";
        return slug.Length <= 100 ? slug : slug[..100].TrimEnd('-');
    }

    private static string Message(Exception error, string fallback) => error is Winference.Core.Runtime.RuntimeException runtime
        ? runtime.Message : fallback;

    private sealed class FamilyBuilder(ModelFamily family)
    {
        public ModelFamily Family { get; } = family;
        public List<LibraryProfile> Profiles { get; } = [];

        public void Add(LibraryProfile profile)
        {
            if (Profiles.Any(existing => existing.ProfileId == profile.ProfileId)) return;
            Profiles.Add(profile);
        }
    }
}
