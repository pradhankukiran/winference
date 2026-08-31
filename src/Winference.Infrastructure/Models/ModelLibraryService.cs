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
    }
}
