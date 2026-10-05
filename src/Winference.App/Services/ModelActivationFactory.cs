using Winference.Core.Gpu;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Gpu;
using Winference.Infrastructure.Hybrid;
using Winference.Infrastructure.Ods;
using Winference.Infrastructure.Selection;
using Winference.Infrastructure.Setup;

namespace Winference.App.Services;

/// <summary>A fresh owned session and its activation work, independent of any view.</summary>
internal sealed record ModelChatSession(SetupModel Model, ModelProfile Profile, RuntimeBroker Broker,
    Func<IProgress<SetupProgress>, CancellationToken, Task> ActivateAsync);

internal sealed record NpuModelPreparation(PreparedModel Prepared, ISetupService Service);

/// <summary>
/// Creates runtime sessions and switch plans without holding a window or control.
/// Presentation callbacks run on the initiating context; the coordinator still owns
/// preflight/unload/load ordering and verifies every returned session identity.
/// </summary>
internal sealed class ModelActivationFactory(
    string dataRoot,
    string applicationDirectory,
    GpuModelService gpuModels,
    GpuRuntimeAcquisition gpuRuntime,
    HybridModelService hybridModels,
    HybridRuntimeAcquisition hybridRuntime,
    ISetupService setupService,
    ActiveModelSelectionStore activeSelection)
{
    public ModelChatSession CreateGpuSession(GpuModelRegistration model, bool allowComponentDownload)
    {
        var installation = gpuRuntime.ExpectedInstallation;
        var broker = new RuntimeBroker(CreateGpuBackend(model), TimeSpan.FromSeconds(65));
        var display = new SetupModel(model.ModelId, model.DisplayName, "Local GGUF", model.Architecture,
            0, model.LengthBytes, "Installed", Path.GetDirectoryName(model.ModelPath), null,
            model.Metadata.MaximumContextLength ?? model.ContextLength, model.ContextLength, "local-gguf-v1");
        return new(display, model.Profile, broker, async (progress, token) =>
        {
            if (allowComponentDownload) await gpuRuntime.AcquireAsync(progress, token);
            else if (!await GpuRuntimeAcquisition.VerifyAsync(installation, token))
                throw new RuntimeException("gpu_runtime_missing", "The GPU component is missing or changed. Choose this model in Models to review its setup.");
            progress.Report(new("LoadingGpu", $"Loading {model.DisplayName} on the GPU…"));
            await broker.StartAsync(model.Profile, token);
            await gpuModels.RememberAsync(model, token);
            await SaveSelectionAsync(new(ExecutionMode.GpuOnly, model.Id), token);
        });
    }

    public ModelChatSession CreateHybridSession(HybridModelRegistration model, bool allowComponentDownload)
    {
        var installation = hybridRuntime.ExpectedInstallation;
        var broker = new RuntimeBroker(CreateHybridBackend(model), TimeSpan.FromSeconds(65));
        var display = new SetupModel(model.ModelId, model.DisplayName, "AMD Ryzen AI model", "Local GPU + NPU model",
            0, model.LengthBytes, "Installed", model.ModelDirectory, null,
            model.ContextLength, model.ContextLength, model.Revision);
        return new(display, model.Profile, broker, async (progress, token) =>
        {
            if (allowComponentDownload) await hybridRuntime.AcquireAsync(progress, token);
            else if (!await HybridRuntimeAcquisition.VerifyAsync(installation, token))
                throw new RuntimeException("hybrid_runtime_missing", "The GPU + NPU component is missing or changed. Choose this model in Models to review its setup.");
            progress.Report(new("LoadingHybrid", $"Loading {model.DisplayName} on the GPU + NPU…"));
            await broker.StartAsync(model.Profile, token);
            await hybridModels.RememberAsync(model, token);
            await SaveSelectionAsync(new(ExecutionMode.GpuNpu, HybridRegistrationId: model.Id), token);
        });
    }

    public ModelChatSession CreateNpuSession(PreparedModel prepared, bool saveSelection, ISetupService? service = null)
    {
        var owner = service ?? setupService;
        return new(prepared.Model, prepared.Profile, prepared.Broker, async (progress, token) =>
        {
            await owner.ActivateAsync(prepared, progress, token);
            if (saveSelection) await owner.SaveAsync(prepared, token);
            await SaveSelectionAsync(new(ExecutionMode.NpuOnly), token);
        });
    }

    public async Task SaveSelectionAsync(ActiveModelSelection selection, CancellationToken token)
    {
        try { await activeSelection.SaveAsync(selection, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeException("selection_save", "Your model choice could not be saved. Check storage and folder access, then Retry. Your chats were kept.", error);
        }
    }

    public ModelActivationPlan CreateGpuPlan(GpuModelRegistration model, bool allowComponentDownload,
        IProgress<SetupProgress> progress, Func<CancellationToken, Task<RuntimeSession?>> showAndLoad,
        Func<ModelProfile, Task> stopMatchingSession) =>
        new(model.Profile, async token =>
        {
            if (allowComponentDownload) await gpuRuntime.AcquireAsync(progress, token);
            RequireAvailable(await CreateGpuBackend(model).DiscoverAsync(model.Profile, token));
            var hardware = await setupService.InspectAsync(token);
            if (!hardware.Graphics.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                throw new RuntimeException("gpu_hardware", "This GPU route requires a supported Radeon adapter.");
        }, token => LoadReadyAsync(showAndLoad, "The GPU model did not become ready.", token),
            token => stopMatchingSession(model.Profile));

    public ModelActivationPlan CreateHybridPlan(HybridModelRegistration model, bool allowComponentDownload,
        IProgress<SetupProgress> progress, Func<CancellationToken, Task<RuntimeSession?>> showAndLoad,
        Func<ModelProfile, Task> stopMatchingSession) =>
        new(model.Profile, async token =>
        {
            if (allowComponentDownload) await hybridRuntime.AcquireAsync(progress, token);
            RequireAvailable(await CreateHybridBackend(model).DiscoverAsync(model.Profile, token));
        }, token => LoadReadyAsync(showAndLoad, "The GPU + NPU model did not become ready.", token),
            token => stopMatchingSession(model.Profile));

    public ModelActivationPlan CreateNpuPlan(SetupModel model, ModelProfile profile, IProgress<SetupProgress> progress,
        Func<PreparedModel, ISetupService, CancellationToken, Task<RuntimeSession?>> showAndLoad,
        Func<ModelProfile, Task> stopMatchingSession, PreparedModel? prepared = null, ISetupService? service = null)
    {
        var descriptorService = service ?? CreateNpuDescriptorService();
        return new(profile, async token =>
        {
            prepared ??= await descriptorService.OpenInstalledAsync(new(model.Id, ExecutionMode.NpuOnly,
                model.ExistingModelRoot!, profile.ContextLength), progress, token);
            if (prepared.Profile != profile)
                throw new RuntimeException("npu_profile_changed", "The selected NPU model changed. Choose it again before loading.");
        }, token => LoadReadyAsync(innerToken => showAndLoad(prepared!, descriptorService, innerToken),
            "The NPU model did not become ready.", token), token => stopMatchingSession(profile));
    }

    public async Task<NpuModelPreparation> PrepareNpuAsync(SetupModel model, int contextLength,
        IProgress<SetupProgress> progress, CancellationToken token)
    {
        // Descriptor checks must not replace the service that owns a running NPU session.
        var service = CreateNpuDescriptorService();
        var prepared = await service.OpenInstalledAsync(new(model.Id, ExecutionMode.NpuOnly,
            model.ExistingModelRoot!, contextLength), progress, token);
        return new(prepared, service);
    }

    private OdsGpuBackend CreateGpuBackend(GpuModelRegistration model) => new(model, gpuRuntime.ExpectedInstallation,
        Path.Combine(applicationDirectory, "Runtimes", "OdsHelpers"), Path.Combine(dataRoot, "Sessions", "Gpu"));

    private HybridRyzenAiBackend CreateHybridBackend(HybridModelRegistration model) => new(model, hybridRuntime.ExpectedInstallation,
        Environment.ProcessPath ?? Path.Combine(applicationDirectory, "Winference.exe"), Path.Combine(dataRoot, "Sessions", "Hybrid"));

    private static ISetupService CreateNpuDescriptorService() => new FirstRunSetupService(SetupPaths.CreateDefault());

    private static async Task<RuntimeSession> LoadReadyAsync(Func<CancellationToken, Task<RuntimeSession?>> showAndLoad,
        string failureDetail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await showAndLoad(token) ?? throw new RuntimeException("not_ready", failureDetail);
    }

    private static void RequireAvailable(RuntimeAvailability availability)
    {
        if (!availability.IsAvailable)
            throw new RuntimeException(availability.FailureCode ?? "model_unavailable", availability.Detail);
    }
}
