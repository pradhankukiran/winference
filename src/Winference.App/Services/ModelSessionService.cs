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

    private DispatcherQueueTimer? _idleWatch;

    public bool IsChanging => _change is { IsCompleted: false };

    /// <summary>How long a loaded model may sit unused. Null keeps it loaded.</summary>
    public TimeSpan? IdleTimeout { get; set; }

    public void StartIdleWatch()
    {
        _idleWatch = dispatcher.CreateTimer();
        _idleWatch.Interval = TimeSpan.FromSeconds(10);
        _idleWatch.IsRepeating = true;
        _idleWatch.Tick += (_, _) =>
        {
            if (!lifetime.IsClosing && !IsChanging) shell.Chat?.UnloadIfIdle(IdleTimeout);
        };
        _idleWatch.Start();
    }
    public Task? PendingChange => _change;

    private static ModeSwitchCoordinator CreateCoordinator(ShellViewModel shell, AppLifetime lifetime)
    {
        var switches = new ModeSwitchCoordinator();
        switches.Changed += (_, state) =>
        {
            AppDiagnostics.Write("model.switch", new { operation = state.OperationId,
                stage = state.Stage.ToString(), requested = state.RequestedProfile?.ExecutionMode.ToString(),
                active = state.ActiveProfile?.ExecutionMode.ToString(), recovery = state.Recovery.ToString() });
            if (!lifetime.IsClosing && state.Stage is not (ModeSwitchStage.Ready or ModeSwitchStage.Failed))
                shell.ReportModelActivity(state.Detail);
        };
        return switches;
    }

    /// <summary>Runs one model change. A second request while one is running is ignored.</summary>
    public async Task RunAsync(Func<Task> change)
    {
        if (lifetime.IsClosing || IsChanging) return;
        _change = change();
        await _change;
    }

    /// <summary>Opens the saved model. False means this is a true first run.</summary>
    public async Task<bool> OpenSavedAsync(bool loadInBackground)
    {
        try { return await TryOpenSavedAsync(loadInBackground); }
        catch (OperationCanceledException) when (lifetime.IsClosing) { return true; }
        catch (Exception error)
        {
            if (!lifetime.IsClosing) ShowUnavailable(error);
            return true;
        }
    }

    private async Task<bool> TryOpenSavedAsync(bool loadInBackground)
    {
        var token = lifetime.Token;
        var selected = await activeSelection.ReadAsync(token);
        try
        {
            if (selected?.Mode == ExecutionMode.GpuNpu)
            {
                var hybrid = await hybridModels.ReadRememberedAsync(token);
                if (hybrid is not null && (selected.HybridRegistrationId is null || hybrid.Id == selected.HybridRegistrationId))
                {
                    ShowHybridChat(hybrid, loadInBackground);
                    return true;
                }
            }
            else if (selected?.Mode == ExecutionMode.GpuOnly)
            {
                var gpu = await gpuModels.ReadRememberedAsync(token);
                if (gpu is not null && (selected.GpuRegistrationId is null || gpu.Id == selected.GpuRegistrationId))
                {
                    ShowGpuChat(gpu, loadInBackground);
                    return true;
                }
            }
            else
            {
                var remembered = await setup.OpenRememberedAsync(token);
                if (remembered is not null)
                {
                    ShowPreparedChat(remembered, loadInBackground);
                    return true;
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AppDiagnostics.Write("active.model.failed", new { mode = selected?.Mode.ToString(), message = error.Message });
        }

        // Never load a different model on the user's behalf. Returning users keep their
        // chats and pick the next model themselves; only a true first run opens setup.
        if (selected is not null || conversations.Workspace.Conversations.Count > 0)
            throw new RuntimeException("model_unavailable", "Your last model isn't available. Choose a model to keep chatting. Your chats were kept.");
        return false;
    }

    public void ShowUnavailable(Exception error, bool navigate = true)
    {
        _currentActivation = null;
        shell.Chat?.DetachIfUnavailable();
        // Without trustworthy model metadata, keep chats readable and offer the library.
        var saved = conversations.Workspace.Conversations.FirstOrDefault(item => item.Id == conversations.Workspace.ActiveConversationId);
        var modelId = saved?.ModelId ?? "unavailable";
        var mode = modelId.StartsWith("gguf:", StringComparison.Ordinal) ? ExecutionMode.GpuOnly
            : modelId.StartsWith("hybrid:", StringComparison.Ordinal) ? ExecutionMode.GpuNpu : ExecutionMode.NpuOnly;
        var profile = new ModelProfile(modelId, saved?.ProfileId ?? "unavailable", saved?.ArtifactId ?? "unavailable",
            "unavailable", mode, 2048);
        shell.SelectModel(new(modelId, ShellViewModel.NoModelName, string.Empty, string.Empty, null, null,
            "Unavailable", null, "Choose a model to continue.", 4096, 4096, string.Empty), profile);
        var chat = new ChatViewModel(shell, new RuntimeBroker(new UnavailableBackend()), profile, dispatcher,
            conversations, modelId, allowLoad: false, features: features);
        chat.ReportLoadFailure(error is RuntimeException runtime ? runtime.Message : "The saved model information could not be opened. Choose a model to continue.");
        shell.AttachChat(chat);
        navigator.ShowChat(navigate);
    }

    public Task UseGpuAsync(GpuModelRegistration model) => RunAsync(() => ReviewGpuAsync(model));
    public Task UseHybridAsync(HybridModelRegistration model) => RunAsync(() => ReviewHybridAsync(model));
    public Task UseNpuAsync(SetupModel model) => RunAsync(() => SwitchNpuAsync(model));

    /// <summary>Stops the loaded model before its files are removed. Chats stay open and readable.</summary>
    public async Task ReleaseAsync(string modelId)
    {
        if (shell.ActiveModelId != modelId) return;
        if (shell.Chat is { } chat) await chat.ShutdownAsync();
        await setup.StopAsync(lifetime.Token);
        await conversations.FlushAsync();
        ShowUnavailable(new RuntimeException("model_removed",
            "No model is loaded. Choose a model to keep chatting. Your chats were kept."), navigate: false);
    }

    private void ShowPreparedChat(PreparedModel prepared, bool loadInBackground, bool saveSelection = false, ISetupService? service = null)
    {
        var session = activations.CreateNpuSession(prepared, saveSelection, service);
        ShowRuntimeChat(session.Model, session.Profile, session.Broker, session.ActivateAsync, loadInBackground);
        _currentActivation = CreateNpuActivation(prepared.Model, prepared.Profile);
    }

    private void ShowGpuChat(GpuModelRegistration model, bool loadInBackground, bool allowComponentDownload = false)
    {
        var session = activations.CreateGpuSession(model, allowComponentDownload);
        ShowRuntimeChat(session.Model, session.Profile, session.Broker, session.ActivateAsync, loadInBackground);
        _currentActivation = CreateGpuActivation(model);
    }

    private void ShowHybridChat(HybridModelRegistration model, bool loadInBackground, bool allowComponentDownload = false)
    {
        var session = activations.CreateHybridSession(model, allowComponentDownload);
        ShowRuntimeChat(session.Model, session.Profile, session.Broker, session.ActivateAsync, loadInBackground);
        _currentActivation = CreateHybridActivation(model);
    }
}
