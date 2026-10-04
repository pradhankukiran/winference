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
}
