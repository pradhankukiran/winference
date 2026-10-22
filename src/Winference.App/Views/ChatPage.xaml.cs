using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using Winference.App.Controls;
using Winference.App.Services;
using Winference.App.ViewModels;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Winference.App.Views;

public sealed partial class ChatPage : Page
{
    public event EventHandler? ChooseModelRequested;
    private bool _initialized;
    private readonly ChatViewModel? _chat;
    private readonly ModelLibraryViewModel _library;
    private readonly ReadAloudService _readAloud;
    private readonly DispatcherQueueTimer _scrollTimer;
    private readonly DispatcherQueueTimer _announcementTimer;
    private readonly HashSet<ChatMessageViewModel> _messageSubscriptions = [];
    private bool _subscribed;
    private bool _followLatest = true;
    private bool _hasUnseenOutput;
    private bool _wasGenerating;
    private bool _isComposing;
    private bool _suppressEnterUntilRelease;
    private bool _keyboardFocusMove;
    private double _lastVerticalOffset;

    public ShellViewModel ViewModel { get; }

    public void FocusComposer()
    {
        if (IsLoaded && Composer.IsEnabled) Composer.Focus(FocusState.Programmatic);
    }

    /// <summary>Opens the model picker from the keyboard.</summary>
    public void OpenModelPicker()
    {
        if (ChooseModelButton.IsEnabled) ModelFlyout.ShowAt(ChooseModelButton);
    }

    public ChatPage(ShellViewModel viewModel, ModelLibraryViewModel library, ReadAloudService readAloud)
    {
        ViewModel = viewModel;
        _library = library;
        _readAloud = readAloud;
        InitializeComponent();
        DataContext = viewModel;
        var picker = new ModelListControl(library, compact: true);
        picker.ActionInvoked += (_, _) => ModelFlyout.Hide();
        ModelListHost.Content = picker;
        _chat = viewModel.Chat;
        _scrollTimer = DispatcherQueue.CreateTimer();
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(60);
        _scrollTimer.IsRepeating = false;
        _scrollTimer.Tick += ScrollTimer_Tick;
        _announcementTimer = DispatcherQueue.CreateTimer();
        _announcementTimer.Interval = TimeSpan.FromSeconds(4);
        _announcementTimer.IsRepeating = false;
        _announcementTimer.Tick += (_, _) => ResponseAnnouncement.Text = string.Empty;
        _initialized = true;
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        if (_chat is not null)
        {
            _chat.PropertyChanged += Chat_PropertyChanged;
            _chat.Messages.CollectionChanged += Messages_CollectionChanged;
            SubscribeMessages();
            _wasGenerating = _chat.IsGenerating;
        }
    }

    private void ChooseModel_Click(object sender, RoutedEventArgs args)
    {
        ModelFlyout.Hide();
        ChooseModelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ModelFlyoutAction_Click(object sender, RoutedEventArgs args) => ModelFlyout.Hide();

    private void Message_PointerEntered(object sender, PointerRoutedEventArgs args) => ShowActions(sender, true);
    private void Message_PointerExited(object sender, PointerRoutedEventArgs args) => ShowActions(sender, false);
    private void MessageAction_GotFocus(object sender, RoutedEventArgs args) => SetActionsOpacity(sender, true);
    private void MessageAction_LostFocus(object sender, RoutedEventArgs args) => SetActionsOpacity(sender, false);

    private static void SetActionsOpacity(object button, bool visible)
    {
        if (button is FrameworkElement { Parent: UIElement actions } element)
            actions.Opacity = visible || element.DataContext is ChatMessageViewModel { IsSpeaking: true } ? 1 : 0;
    }

    /// <summary>Message actions show on hover or focus, and stay visible while that reply is read aloud.</summary>
    private static void ShowActions(object message, bool visible)
    {
        if (message is FrameworkElement root && root.FindName("MessageActions") is UIElement actions)
            actions.Opacity = visible || root.DataContext is ChatMessageViewModel { IsSpeaking: true } ? 1 : 0;
    }

    private async void EditMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessageViewModel message } || _chat is not { } chat) return;
        var after = chat.MessagesAfter(message);
        if (after > 0)
        {
            var dialog = new ContentDialog
            {
                Title = "Edit this message?",
                Content = new TextBlock
                {
                    Text = $"It goes back into the message box so you can change it. The {after} message{(after == 1 ? "" : "s")} after it will be removed from this chat.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Edit", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot
            };
            try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) return; }
            catch (System.Runtime.InteropServices.COMException) { return; }
        }
        if (!chat.EditMessage(message)) return;
        Composer.Focus(FocusState.Programmatic);
        Composer.SelectionStart = Composer.Text.Length;
        Announce("Your message is back in the box. Change it and press Enter to send.");
    }

    private void Regenerate_Click(object sender, RoutedEventArgs args)
    {
        if (_chat is not { CanRetryReply: true } chat) return;
        _readAloud.Stop();
        chat.RetryCommand.Execute(null);
        Announce("Writing a new reply…");
    }
}
