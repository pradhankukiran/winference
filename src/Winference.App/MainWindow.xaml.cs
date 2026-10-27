using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Winference.App.Diagnostics;
using Winference.App.Presentation;
using Winference.App.Services;
using Winference.App.ViewModels;
using Winference.App.Views;
using Winference.Core.Chat;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Chat;
using Winference.Infrastructure.Gpu;
using Winference.Infrastructure.Hybrid;
using Winference.Infrastructure.Models;
using Winference.Infrastructure.Selection;
using Winference.Infrastructure.Setup;

namespace Winference.App;

/// <summary>The window: page navigation, shortcuts, theme and closing. Model work lives in the services.</summary>
public sealed partial class MainWindow : Window, IShellNavigator
{
    private readonly AppLifetime _lifetime = new();
    private readonly string _dataRoot;
    private readonly ISetupService _setupService;
    private readonly ConversationSession _conversations;
    private readonly GpuRuntimeAcquisition _gpuRuntime;
    private readonly HybridRuntimeAcquisition _hybridRuntime;
    private readonly AppPreferencesStore _preferences;
    private readonly DialogService _dialogs;
    private readonly ModelLibraryViewModel _library;
    private readonly ModelSessionService _session;
    private readonly ModelsPage _modelsPage;
    private readonly SettingsPage _settingsPage;
    private readonly WelcomePage _welcomePage;
    private readonly ReadAloudService _readAloud;
    private readonly Winference.Infrastructure.Lemonade.LemonadeMediaService _media;
    private ChatPage? _chatPage;
    private bool _started;
    private bool _initialized;
    private bool _cleanupComplete;

    public ShellViewModel ViewModel { get; } = new();

    public ShellNavigationItem[] NavigationItems { get; } =
    [
        new(ShellPage.Models, "Models", "", "NavModels"),
        new(ShellPage.Settings, "Settings", "", "NavSettings")
    ];

    public MainWindow()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        var paths = SetupPaths.CreateDefault();
        var hardware = TestHardware.Override();
        _dataRoot = paths.DataRoot;
        _setupService = new FirstRunSetupService(paths, hardware);
        _conversations = new ConversationSession(new JsonConversationStore(_dataRoot));
        var gpuModels = new GpuModelService(_dataRoot);
        _gpuRuntime = new GpuRuntimeAcquisition(_dataRoot);
        var hybridModels = new HybridModelService(_dataRoot);
        _hybridRuntime = new HybridRuntimeAcquisition(_dataRoot);
        var modelLibrary = new ModelLibraryService(paths, _setupService, gpuModels, hybridModels);
        var activeSelection = new ActiveModelSelectionStore(_dataRoot);
        var activations = new ModelActivationFactory(_dataRoot, AppContext.BaseDirectory, gpuModels, _gpuRuntime,
            hybridModels, _hybridRuntime, _setupService, activeSelection);
        var conversationCatalog = ConversationCompatibilityCatalog.Load(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Catalogs", "conversation-compatibility.json"));
        _preferences = new AppPreferencesStore(_dataRoot);
        InitializeComponent();
        _dialogs = new DialogService(() => RootLayout.XamlRoot, queue, _lifetime);
        // Downloads use their own service so a long pull cannot block loading the current model.
        _library = new ModelLibraryViewModel(ViewModel, _setupService, new FirstRunSetupService(paths, hardware), modelLibrary,
            gpuModels, _gpuRuntime, hybridModels, _hybridRuntime, _preferences, _dataRoot, _dialogs, _lifetime, queue, PickModelFileAsync);
        _media = new Winference.Infrastructure.Lemonade.LemonadeMediaService(paths.LemonadePath, _dataRoot);
        var mediaSelection = new MediaSelection(_media, _preferences, queue);
        ChatMessageViewModel.ImagesRoot = System.IO.Path.Combine(_dataRoot, "Images");
        _readAloud = new ReadAloudService(queue, mediaSelection);
        var tools = new Winference.Core.Tools.ToolExecutionBroker();
        tools.RegisterRange(Winference.Core.Tools.BuiltInTools.GetAll());
        var features = new ChatFeatures(new Winference.Infrastructure.Chat.ConversationAttachmentStore(_dataRoot), tools,
            () => _preferences.Read().ToolsEnabled, ConfirmToolAsync, mediaSelection, ChatMessageViewModel.ImagesRoot,
            () => _preferences.Read().CustomInstructions,
            () => Winference.Core.Runtime.ResponseStyles.TemperatureFor(_preferences.Read().ResponseStyle));
        _session = new ModelSessionService(ViewModel, _conversations, activations, _setupService, gpuModels, _gpuRuntime,
            hybridModels, _hybridRuntime, activeSelection, conversationCatalog, queue, _dialogs, _lifetime, this, features);
        _library.Attach(_session);
        _modelsPage = new ModelsPage(_library);
        _welcomePage = new WelcomePage(_library);
        _welcomePage.AllModelsRequested += (_, _) => ShowShell(ShellPage.Models);
        _settingsPage = new SettingsPage(_dataRoot);
        _settingsPage.RefreshRequested += Settings_RefreshRequested;
        _settingsPage.AppearanceChanged += Settings_AppearanceChanged;
        var preferences = _preferences.Read();
        _settingsPage.SetModelBehavior(preferences.IdleUnloadMinutes, preferences.LoadOnStart);
        _settingsPage.SetToolsEnabled(preferences.ToolsEnabled);
        _settingsPage.SetChatPreferences(preferences.CustomInstructions, preferences.ResponseStyle);
        _settingsPage.InstructionsChanged += (_, text) => _preferences.Write(_preferences.Read() with { CustomInstructions = text });
        _settingsPage.ResponseStyleChanged += (_, style) => _preferences.Write(_preferences.Read() with { ResponseStyle = style });
        _settingsPage.AttachAddons(new MediaAddonsViewModel(mediaSelection,
            (title, lines, primary, isDefault) => _dialogs.ConfirmAsync(title, lines, primary, isDefault), _dialogs.InformAsync));
        _settingsPage.ToolsEnabledChanged += (_, enabled) => _preferences.Write(_preferences.Read() with { ToolsEnabled = enabled });
        _settingsPage.IdleUnloadChanged += Settings_IdleUnloadChanged;
        _settingsPage.LoadOnStartChanged += Settings_LoadOnStartChanged;
        ApplyIdleTimeout(preferences.IdleUnloadMinutes);
        _session.StartIdleWatch();
        _settingsPage.OpenLogsRequested += (_, _) => App.OpenLogsFolder();
        _settingsPage.OpenDataFolderRequested += (_, _) => App.OpenFolder(_dataRoot);
        _initialized = true;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Winference.ico"));
        ConfigureWindowBounds();
        RestoreAppearance();
        SyncCaptionTheme();
        AppWindow.Closing += AppWindow_Closing;
    }
}
