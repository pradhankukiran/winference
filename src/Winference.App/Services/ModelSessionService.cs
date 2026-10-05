using Microsoft.UI.Dispatching;
using Winference.App.Diagnostics;
using Winference.App.ViewModels;
using Winference.Core.Chat;
using Winference.Core.Gpu;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Gpu;
using Winference.Infrastructure.Hybrid;
using Winference.Infrastructure.Models;
using Winference.Infrastructure.Selection;

namespace Winference.App.Services;

/// <summary>What the session needs from the window, without knowing it is a window.</summary>
internal interface IShellNavigator
{
    bool IsChatVisible { get; }
    void ShowChat(bool navigate);
    void ShowModels();
    void FocusComposer();
}

/// <summary>
/// Owns which model is loaded: opening the saved choice, switching, recovering from
/// failure and stopping. Only one change runs at a time, and it never picks a model
/// the user did not choose.
/// </summary>
internal sealed class ModelSessionService(
    ShellViewModel shell,
    ConversationSession conversations,
    ModelActivationFactory activations,
    ISetupService setup,
    GpuModelService gpuModels,
    GpuRuntimeAcquisition gpuRuntime,
    HybridModelService hybridModels,
    HybridRuntimeAcquisition hybridRuntime,
    ActiveModelSelectionStore activeSelection,
    ConversationCompatibilityCatalog conversationCatalog,
    DispatcherQueue dispatcher,
    DialogService dialogs,
    AppLifetime lifetime,
    IShellNavigator navigator,
    ChatFeatures features)
{
    private readonly ModeSwitchCoordinator _switches = CreateCoordinator(shell, lifetime);
    private ModelActivationPlan? _currentActivation;
    private Task? _change;
}
