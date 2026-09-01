using Winference.Core.Downloads;
using Winference.Core.Gpu;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Setup;
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

    public ModelLibraryService(SetupPaths paths, ISetupService setup, GpuModelService gpuModels,
        HybridModelService hybridModels, HttpClient? http = null, string? gpuCatalogPath = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
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

        AddGpuCatalog(families, gpuModels);
        AddRegisteredGpu(families, gpuModels);
        if (inspection is not null) AddFlm(families, inspection);
        AddHybrid(families, hybridModels, inspection);
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
            var family = FamilyForLocalGguf(model);
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
                    : "Registration metadata exists, but its file is missing or changed. No model file was modified.");
            Get(families, family).Add(profile);
        }
    }

    private static void AddFlm(Dictionary<string, FamilyBuilder> families, SetupInspection inspection)
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
                "Lemonade 2026.42.1 + FastFlowLM 1.0.10",
                new("flm:" + model.Id, family, ModelArtifactFormat.FastFlowLm, "FastFlowLM package",
                    "FastFlowLM 1.0.10 catalog", model.CatalogRevision, model.StorageBytes, null,
                    "Lemonade + FastFlowLM 1.0.10"),
                new(state, summary, unavailable),
                installed ? 0 : model.DownloadBytes, model.StorageBytes, model.ExistingModelRoot,
                state == ModelAvailability.Installed, state == ModelAvailability.DownloadRequired, false,
                "FastFlowLM catalog entry with supplier file sizes. Local NPU answer quality is not implied.");
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
}
