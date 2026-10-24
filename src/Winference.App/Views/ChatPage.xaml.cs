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

    private async void ReadAloud_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessageViewModel message }) return;
        var error = await _readAloud.ToggleAsync(message);
        Announce(error ?? (message.IsSpeaking ? "Reading aloud." : "Stopped reading."));
    }

    private Windows.Media.Capture.MediaCapture? _capture;
    private Windows.Storage.Streams.InMemoryRandomAccessStream? _recording;
    private DispatcherQueueTimer? _recordingLimit;
    private bool _transcribing;

    private void Image_Click(object sender, RoutedEventArgs args)
    {
        if (_chat is not { CanStartImage: true } chat) return;
        if (!chat.DraftMessage.StartsWith(ChatViewModel.ImageCommand.TrimEnd(), StringComparison.OrdinalIgnoreCase))
            chat.DraftMessage = ChatViewModel.ImageCommand + chat.DraftMessage.TrimStart();
        Composer.Focus(FocusState.Programmatic);
        Composer.SelectionStart = Composer.Text.Length;
        Announce("Describe the picture, then send.");
    }

    /// <summary>First click records from the microphone; the second stops and adds the words to the draft.</summary>
    private async void Mic_Click(object sender, RoutedEventArgs args)
    {
        if (_transcribing || _chat is not { CanDictate: true } chat) return;
        if (_capture is null) await StartRecordingAsync();
        else await StopRecordingAsync(chat);
    }

    private async Task StartRecordingAsync()
    {
        _readAloud.Stop();
        try
        {
            _capture = new Windows.Media.Capture.MediaCapture();
            await _capture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio,
                MediaCategory = Windows.Media.Capture.MediaCategory.Speech
            });
            var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateWav(Windows.Media.MediaProperties.AudioEncodingQuality.Low);
            profile.Audio = Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm(16000, 1, 16);
            _recording = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await _capture.StartRecordToStreamAsync(profile, _recording);
            MicGlyph.Glyph = "\uE71A";
            AutomationProperties.SetName(MicButton, "Stop recording");
            ToolTipService.SetToolTip(MicButton, "Stop recording");
            _recordingLimit ??= DispatcherQueue.CreateTimer();
            _recordingLimit.Interval = TimeSpan.FromMinutes(2);
            _recordingLimit.IsRepeating = false;
            _recordingLimit.Tick += RecordingLimit_Tick;
            _recordingLimit.Start();
            Announce("Listening… click the microphone again when you're done.");
        }
        catch (Exception error) when (error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005))
        {
            ResetRecording();
            await ShowProblemAsync("Microphone not available", ("Winference isn't allowed to use the microphone. In Windows Settings > Privacy & security > Microphone, turn on \"Let desktop apps access your microphone\"."));
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException)
        {
            ResetRecording();
            await ShowProblemAsync("Microphone not available", "No microphone was found, or another app is using it.");
        }
    }

    private async void RecordingLimit_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Tick -= RecordingLimit_Tick;
        if (_capture is not null && _chat is { } chat) await StopRecordingAsync(chat);
    }

    private async Task StopRecordingAsync(ChatViewModel chat)
    {
        if (_recordingLimit is { } limit) { limit.Stop(); limit.Tick -= RecordingLimit_Tick; }
        var capture = _capture;
        var recording = _recording;
        _capture = null;
        _recording = null;
        if (capture is null || recording is null) { ResetRecording(); return; }
        _transcribing = true;
        MicGlyph.Glyph = "\uE916";
        Announce("Turning your speech into text…");
        try
        {
            await capture.StopRecordAsync();
            var bytes = new byte[recording.Size];
            recording.Seek(0);
            using (var reader = new Windows.Storage.Streams.DataReader(recording))
            {
                await reader.LoadAsync((uint)recording.Size);
                reader.ReadBytes(bytes);
            }
            if (bytes.Length < 16000) { Announce("That recording was too short."); return; }
            var text = await chat.TranscribeAsync(bytes, CancellationToken.None);
            if (text.Length == 0 || text.Trim('.', ' ').Equals("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase))
            {
                Announce("No speech was heard. Check your microphone and try again.");
                return;
            }
            var draft = chat.DraftMessage;
            chat.DraftMessage = draft.Length == 0 ? text : draft.TrimEnd() + " " + text;
            Composer.Focus(FocusState.Programmatic);
            Composer.SelectionStart = Composer.Text.Length;
            Announce("Added what you said. Edit it or press Enter to send.");
        }
        catch (Winference.Core.Runtime.RuntimeException error) { await ShowProblemAsync("Speech wasn't turned into text", error.Message); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException)
        {
            await ShowProblemAsync("Speech wasn't turned into text", "The recording couldn't be finished. Try again.");
        }
        finally
        {
            _transcribing = false;
            capture.Dispose();
            recording.Dispose();
            ResetRecording();
        }
    }

    private void ResetRecording()
    {
        _capture?.Dispose();
        _capture = null;
        _recording?.Dispose();
        _recording = null;
        MicGlyph.Glyph = "\uE720";
        AutomationProperties.SetName(MicButton, "Speak a message");
        ToolTipService.SetToolTip(MicButton, "Speak a message (click again to stop)");
    }

    private async void Attach_Click(object sender, RoutedEventArgs args)
    {
        if (_chat is not { CanAttach: true } chat) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };
            foreach (var extension in WindowsDocumentReader.SupportedExtensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var files = await picker.PickMultipleFilesAsync();
            await AttachFilesAsync(chat, files.Select(file => file.Path));
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Announce("The file picker couldn't open. Try dropping the file onto the message box instead.");
        }
        Composer.Focus(FocusState.Programmatic);
    }

    private void Composer_DragOver(object sender, DragEventArgs args)
    {
        if (_chat is { CanAttach: true } && args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            args.DragUIOverride.Caption = "Attach to this chat";
        }
        else args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
    }

    private async void Composer_Drop(object sender, DragEventArgs args)
    {
        if (_chat is not { CanAttach: true } chat ||
            !args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var deferral = args.GetDeferral();
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            deferral.Complete();
            deferral = null;
            await AttachFilesAsync(chat, items.OfType<Windows.Storage.StorageFile>().Select(file => file.Path));
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Announce("The dropped files couldn't be read.");
        }
        finally { deferral?.Complete(); }
    }

    private async Task AttachFilesAsync(ChatViewModel chat, IEnumerable<string> paths)
    {
        var problems = new List<string>();
        var added = 0;
        foreach (var path in paths.Where(path => !string.IsNullOrEmpty(path)))
        {
            if (!chat.CanAttach)
            {
                problems.Add($"A chat can have up to {Winference.Infrastructure.Chat.ConversationAttachmentStore.MaxAttachmentsPerChat} documents.");
                break;
            }
            // A vision model sees the picture itself; other models get the text read from it.
            if (chat.SupportsImages && ImagePreparation.IsImage(path))
            {
                if (await chat.AttachImageAsync(path) is { } imageProblem) problems.Add(imageProblem);
                else Announce("Picture added. Ask about it and send.");
                continue;
            }
            if (await chat.AttachAsync(path) is { } problem) problems.Add(problem);
            else added++;
        }
        if (added > 0) Announce(added == 1 ? "Document attached. The model will use it when you send." : $"{added} documents attached.");
        if (problems.Count > 0) await ShowAttachProblemsAsync(problems);
    }

    private async Task ShowAttachProblemsAsync(IReadOnlyList<string> problems)
    {
        var details = new StackPanel { Spacing = 8 };
        foreach (var problem in problems.Distinct()) details.Children.Add(new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            Title = problems.Count == 1 ? "This document couldn't be attached" : "Some documents couldn't be attached",
            Content = details, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
        };
        try { await dialog.ShowAsync(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException) { Announce(problems[0]); }
    }

    private async Task ShowProblemAsync(string title, string problem)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
        };
        try { await dialog.ShowAsync(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException) { Announce(problem); }
    }

    private void RemovePendingImage_Click(object sender, RoutedEventArgs args)
    {
        _chat?.RemovePendingImage();
        Announce("Picture removed.");
        Composer.Focus(FocusState.Programmatic);
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: AttachmentItem item } || _chat is null) return;
        _chat.RemoveAttachment(item.Id);
        Announce($"Removed {item.FileName}.");
        Composer.Focus(FocusState.Programmatic);
    }
    private void ModelFlyout_Opening(object sender, object args) => _ = _library.RefreshAsync();

    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        Subscribe();
        UpdateConversationLayout(scrollToEnd: false);
        UpdateRuntimeActions();
        UpdateLatestAction();
        Composer.Focus(FocusState.Programmatic);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        _readAloud.Stop();
        if (_capture is not null) { _recordingLimit?.Stop(); ResetRecording(); }
        _scrollTimer.Stop();
        _announcementTimer.Stop();
        _isComposing = false;
        _suppressEnterUntilRelease = false;
        _keyboardFocusMove = false;
        PageScroll.BringIntoViewOnFocusChange = true;
        if (!_subscribed) return;
        _subscribed = false;
        if (_chat is null) return;
        _chat.PropertyChanged -= Chat_PropertyChanged;
        _chat.Messages.CollectionChanged -= Messages_CollectionChanged;
        foreach (var message in _messageSubscriptions) message.PropertyChanged -= Message_PropertyChanged;
        _messageSubscriptions.Clear();
    }

    private void TranscriptViewport_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Only the transcript scrolls. Header and composer are finite grid rows.
        TranscriptCanvas.MinHeight = Math.Max(0, args.NewSize.Height);
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessageViewModel message }) return;
        try
        {
            var content = new Windows.ApplicationModel.DataTransfer.DataPackage();
            content.SetText(message.DisplayText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
            Announce("Message copied.");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Announce("The message could not be copied. Select its text and try again.");
        }
    }

    private void Transcript_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Formatted blocks can grow after the raw-text change was batched.
        // Follow only if the reader has not deliberately scrolled away.
        if (_initialized && IsLoaded && args.NewSize.Height != args.PreviousSize.Height) QueueTranscriptScroll();
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Tab) return;
        // A deliberate keyboard focus move may reveal its control. Automatic
        // focus recovery from a disabled composer must not move the reader.
        _keyboardFocusMove = true;
        PageScroll.BringIntoViewOnFocusChange = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _keyboardFocusMove = false;
            PageScroll.BringIntoViewOnFocusChange = _followLatest;
        });
    }

    private void Page_KeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Tab)
        {
            _keyboardFocusMove = false;
            PageScroll.BringIntoViewOnFocusChange = _followLatest;
        }
    }

    private void Composer_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _isComposing = true;
    }

    private void Composer_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _isComposing = false;
        // Some input methods commit before the key reaches the TextBox. The
        // same physical Enter must not also submit the committed composition.
        _suppressEnterUntilRelease |= IsKeyDown(VirtualKey.Enter);
    }

    private void Composer_KeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter) _suppressEnterUntilRelease = false;
    }

    private void Composer_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || IsKeyDown(VirtualKey.Shift)) return;
        if (_isComposing || _suppressEnterUntilRelease)
        {
            _suppressEnterUntilRelease = true;
            return; // Leave the key to the native IME rather than submitting.
        }
        // Enter has one meaning outside composition. While loading/busy it
        // must not insert an unexpected newline or queue a delayed submission.
        args.Handled = true;
        if (_chat?.SendCommand.CanExecute(null) != true) return;
        _followLatest = true;
        _chat.SendCommand.Execute(null);
    }

    private void Chat_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChatViewModel.HasConversation))
            UpdateConversationLayout(scrollToEnd: true);
        if (args.PropertyName is nameof(ChatViewModel.IsConnecting) or nameof(ChatViewModel.IsGenerating) or
            nameof(ChatViewModel.ShowConnectAction) or nameof(ChatViewModel.ShowDisconnectAction) or nameof(ChatViewModel.HasNotice) or
            nameof(ChatViewModel.ShowRetryReply))
            UpdateRuntimeActions();
        if (args.PropertyName == nameof(ChatViewModel.IsGenerating) && _chat is not null)
        {
            if (_wasGenerating && !_chat.IsGenerating)
            {
                var lastReply = _chat.Messages.LastOrDefault(message => !message.IsUser);
                var status = lastReply?.Status;
                Announce(string.IsNullOrWhiteSpace(status) ? "Reply finished."
                    : status.StartsWith("Reply ", StringComparison.OrdinalIgnoreCase) ? status.TrimEnd('.') + "."
                    : $"Reply {status.TrimEnd('.').ToLowerInvariant()}.");
                FrameworkElementAutomationPeer.FromElement(ResponseAnnouncement)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                if (_followLatest && IsLoaded) Composer.Focus(FocusState.Programmatic);
            }
            else if (_chat.IsGenerating) Announce(string.Empty);
            _wasGenerating = _chat.IsGenerating;
        }
    }
}
