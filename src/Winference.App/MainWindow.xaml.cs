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

    bool IShellNavigator.IsChatVisible => ShellLayout.Visibility == Visibility.Visible && ReferenceEquals(PageHost.Content, _chatPage);

    void IShellNavigator.ShowChat(bool navigate)
    {
        _chatPage = new ChatPage(ViewModel, _library, _readAloud);
        _chatPage.ChooseModelRequested += (_, _) => ShowShell(ShellPage.Models);
        if (ViewModel.Chat is { } chat) chat.ConversationQuery = ConversationSearch.Text;
        if (navigate) ShowShell(ShellPage.Chat);
    }

    void IShellNavigator.ShowModels() => ShowShell(ShellPage.Models);
    void IShellNavigator.FocusComposer() => _chatPage?.FocusComposer();

    private void ShowShell(ShellPage page)
    {
        if (_lifetime.IsClosing) return;
        SetupHost.Visibility = Visibility.Collapsed;
        ShellLayout.Visibility = Visibility.Visible;
        PageHost.Content = page switch
        {
            ShellPage.Models => _modelsPage,
            ShellPage.Settings => _settingsPage,
            _ => (object?)_chatPage ?? _modelsPage
        };
        FooterNavigation.SelectedItem = PageHost.Content switch
        {
            ModelsPage => NavigationItems[0],
            SettingsPage => NavigationItems[1],
            _ => null
        };
    }

    private async Task StartAsync()
    {
        try { await _conversations.LoadAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ViewModel.ReportShellError("Saved chats could not be opened. Check this app's storage access before closing."); }
        if (_lifetime.IsClosing) return;
        if (_conversations.Warning is { } warning) ViewModel.ReportShellError(warning);
        var opened = await _session.OpenSavedAsync(loadInBackground: _preferences.Read().LoadOnStart);
        if (TestHardware.StartPage() is { } page) ShowShell(page);
        if (opened || _lifetime.IsClosing) return;
        SetupHost.Content = _welcomePage;
        SetupHost.Visibility = Visibility.Visible;
    }

    /// <summary>Asks before a model uses a tool that can see files on this PC.</summary>
    private Task<bool> ConfirmToolAsync(Winference.Core.Tools.ToolCall call, Winference.Core.Tools.LocalTool tool)
    {
        var target = Winference.Core.Tools.BuiltInTools.ResolvePath(call.Arguments);
        var lines = new List<string> { $"{ViewModel.ModelName} wants to use {call.Name}: {tool.Description}" };
        lines.Add(target is not null ? $"Location: {target}" : $"Details: {call.ArgumentsJson}");
        lines.Add("What it finds is shown only to the model on this PC. Nothing is changed or sent anywhere.");
        return _dialogs.ConfirmAsync("Allow the model to look at this?", [.. lines], "Allow once");
    }

    private async Task<string?> PickModelFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".gguf");
        picker.FileTypeFilter.Add(".json");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var picking = picker.PickSingleFileAsync();
        using var cancel = _lifetime.Token.Register(picking.Cancel);
        return (await picking)?.Path;
    }

    private async void Settings_RefreshRequested(object? sender, EventArgs args)
    {
        if (_lifetime.IsClosing) return;
        _settingsPage.SetBusy(true);
        try
        {
            var inspection = _setupService.InspectAsync(_lifetime.Token);
            var gpu = _gpuRuntime.InspectAsync(_lifetime.Token);
            var hybrid = _hybridRuntime.InspectAsync(_lifetime.Token);
            await Task.WhenAll(inspection, gpu, hybrid);
            if (!_lifetime.IsClosing) _settingsPage.Show(await inspection, await gpu, await hybrid);
        }
        catch (OperationCanceledException) when (_lifetime.IsClosing) { }
        catch (Exception error)
        {
            if (!_lifetime.IsClosing) _settingsPage.ShowError(error is RuntimeException runtime ? runtime.Message : "This PC could not be checked. Try Refresh.");
        }
        finally
        {
            if (!_lifetime.IsClosing) _settingsPage.SetBusy(false);
        }
    }

    private void NewChat_Click(object sender, RoutedEventArgs args)
    {
        ShowShell(ShellPage.Chat);
        _chatPage?.FocusComposer();
    }

    private void Conversation_ItemClick(object sender, ItemClickEventArgs args) => ShowShell(ShellPage.Chat);

    private void SearchToggle_Click(object sender, RoutedEventArgs args)
    {
        if (ConversationSearch.Visibility == Visibility.Visible) HideConversationSearch();
        else ShowConversationSearch();
    }

    private void ShowConversationSearch()
    {
        ConversationSearch.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => ConversationSearch.Focus(FocusState.Keyboard));
    }

    private void HideConversationSearch()
    {
        ConversationSearch.Text = string.Empty;
        ConversationSearch.Visibility = Visibility.Collapsed;
    }

    private void ConversationSearch_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        args.Handled = true;
        HideConversationSearch();
    }

    private void ConversationSearch_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (ViewModel.Chat is { } chat) chat.ConversationQuery = ConversationSearch.Text;
    }

    private void Conversations_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: ConversationListItem item } source) return;
        args.Handled = true;
        // Reusing one menu left its items unresponsive the second time it opened, so each request gets its own.
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += async (_, _) => await RenameConversationAsync(item);
        AutomationProperties.SetAutomationId(rename, "RenameChat");
        var delete = new MenuFlyoutItem { Text = "Delete" };
        delete.Click += async (_, _) => await DeleteConversationAsync(item);
        AutomationProperties.SetAutomationId(delete, "DeleteChat");
        var export = new MenuFlyoutItem { Text = "Export as Markdown…" };
        export.Click += async (_, _) => await ExportConversationAsync(item);
        AutomationProperties.SetAutomationId(export, "ExportChat");
        menu.Items.Add(rename);
        menu.Items.Add(export);
        menu.Items.Add(delete);
        if (args.TryGetPosition(sender, out var point)) menu.ShowAt(sender, point);
        else menu.ShowAt(source);
    }

    /// <summary>Opens the chat if needed, then saves it as a Markdown file the user picks.</summary>
    private async Task ExportConversationAsync(ConversationListItem item)
    {
        if (ViewModel.Chat is not { } chat) return;
        if (chat.SelectedConversation?.Id != item.Id)
        {
            if (!chat.CanChooseConversation) return;
            chat.SelectedConversation = item;
        }
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(chat.ExportFileName)
        };
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try { await System.IO.File.WriteAllTextAsync(file.Path, chat.ExportMarkdown(), new System.Text.UTF8Encoding(false)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await _dialogs.InformAsync("The chat wasn't exported", ["It couldn't be saved there. Choose another folder and try again."]);
        }
    }

    private async Task RenameConversationAsync(ConversationListItem item)
    {
        if (ViewModel.Chat is not { } chat) return;
        var title = await _dialogs.AskTextAsync("Rename chat", item.Title, "Rename");
        if (title is not null) chat.RenameConversation(item.Id, title);
    }

    private async void ConversationList_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Delete || ViewModel.Chat?.SelectedConversation is not { } item) return;
        args.Handled = true;
        await DeleteConversationAsync(item);
    }

    private async Task DeleteConversationAsync(ConversationListItem item)
    {
        if (ViewModel.Chat is not { } chat) return;
        if (!chat.CanChooseConversation)
        {
            ViewModel.ReportShellError("Wait for the current reply to finish before deleting a chat.");
            return;
        }
        if (await _dialogs.ConfirmAsync($"Delete \"{item.Title}\"?", ["This chat will be permanently removed from this PC."], "Delete"))
            chat.DeleteConversation(item.Id);
    }

    private void FooterNavigation_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is ShellNavigationItem item) ShowShell(item.Page);
    }

    private void NewChatShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.Chat is not { CanNewChat: true } chat) return;
        chat.NewChatCommand.Execute(null);
        ShowShell(ShellPage.Chat);
        _chatPage?.FocusComposer();
    }
}
