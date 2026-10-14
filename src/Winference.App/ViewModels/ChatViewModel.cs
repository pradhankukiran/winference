using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Chat;
using Winference.Core.Setup;
using Winference.Core.Rag;
using Winference.Core.Tools;
using Winference.Infrastructure.Chat;

namespace Winference.App.ViewModels;

/// <summary>In-memory chat state backed by one qualified, app-owned runtime profile.</summary>
public sealed class ChatViewModel : INotifyPropertyChanged
{
    private readonly ShellViewModel _shell;
    private readonly RuntimeBroker _broker;
    private readonly ModelProfile _profile;
    private readonly ModelProfile _conversationProfile;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<CancellationToken, Task>? _activateRuntime;
    private readonly bool _allowLoad;
    private readonly ConversationSession? _history;
    private readonly string _modelId;
    private readonly ChatFeatures? _features;
    private LocalVectorIndex _documents = new();
    private string? _embeddedWith;
    private string _documentNotice = string.Empty;
    private bool _attaching;
    private const int MaxToolRounds = 3;
    private Guid _conversationId;
    private ConversationListItem? _selectedConversation;
    private bool _restoringConversation;
    private string _loadingDetail = "Preparing your saved model. You can write while it loads.";
    private RuntimeSnapshot _snapshot;
    private RuntimeAvailability? _availability;
    private CancellationTokenSource? _connectCancellation;
    private CancellationTokenSource? _generationCancellation;
    private Task? _connectTask;
    private Task? _generationTask;
    private bool _connecting;
    private bool _generating;
    private bool _modeTransitioning;
    private bool _shuttingDown;
    private bool _shutdownFailed;
    private bool _cleanupUnconfirmed;
    private bool _disposed;
    private Task? _shutdownTask;
    private string? _operationError;
    private long _selectionRevision;
    private Guid? _activeAttemptId;
    private string _contextNotice = string.Empty;
    private string? _switchDetail;
    private string _conversationQuery = string.Empty;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;

    public ChatViewModel(ShellViewModel shell, RuntimeBroker broker, ModelProfile profile, DispatcherQueue dispatcher,
        ConversationSession? history = null, string? modelId = null, Func<CancellationToken, Task>? activateRuntime = null, bool allowLoad = true,
        ConversationCompatibility? conversationCompatibility = null, ChatFeatures? features = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _profile.Validate();
        _conversationProfile = profile with { ConversationCompatibility = conversationCompatibility ?? profile.ConversationCompatibility };
        _conversationProfile.Validate();
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _snapshot = broker.Snapshot;
        _history = history;
        _modelId = modelId ?? profile.ModelFamilyId;
        _activateRuntime = activateRuntime;
        _allowLoad = allowLoad;
        _features = features;
        if (features?.Media is { } media) media.Changed += Media_Changed;

        ConnectCommand = new AsyncCommand(ConnectAsync, () => CanConnect, HandleCommandFailure);
        DisconnectCommand = new AsyncCommand(DisconnectAsync, () => CanDisconnect, HandleCommandFailure);
        SendCommand = new AsyncCommand(SendAsync, () => CanSend, HandleCommandFailure);
        StopCommand = new AsyncCommand(StopReplyAsync, () => CanStopReply, HandleCommandFailure);
        NewChatCommand = new AsyncCommand(NewChatAsync, () => CanNewChat, HandleCommandFailure);
        RetryCommand = new AsyncCommand(RetryAsync, () => CanRetryReply, HandleCommandFailure);
        Messages.CollectionChanged += Messages_CollectionChanged;

        _broker.Changed += Broker_Changed;
        _shell.PropertyChanged += Shell_PropertyChanged;
        if (_history is not null)
        {
            _history.PropertyChanged += History_PropertyChanged;
            RestoreConversation(_history.Open(_modelId, _conversationProfile));
            SaveConversation();
            RefreshConversations();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<ConversationListItem> Conversations { get; } = [];
    /// <summary>Documents attached to the open chat. Their text is offered to the model with each message.</summary>
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];
    public bool HasAttachments => Attachments.Count > 0;
    private string _attachingDetail = string.Empty;
    /// <summary>Progress while a PDF or picture is read, such as "report.pdf: Reading page 3 of 12…".</summary>
    public string AttachingDetail
    {
        get => _attachingDetail;
        private set
        {
            if (_attachingDetail == value) return;
            _attachingDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReadingDocument));
        }
    }
    public bool IsReadingDocument => _attachingDetail.Length > 0;
    public bool CanAttach => _features is not null && _allowLoad && !_attaching && !_generating && !_shuttingDown &&
        !_modeTransitioning && Attachments.Count < ConversationAttachmentStore.MaxAttachmentsPerChat;
    public bool CanRemoveAttachment => !_generating && !_shuttingDown;
    /// <summary>True when the loaded model can see pictures, so attached pictures are sent as images.</summary>
    public bool SupportsImages => _profile.SupportsImages && _features is not null;

    private string? _pendingImage;
    /// <summary>A picture waiting to go with the next message, relative to the images folder.</summary>
    public string? PendingImage
    {
        get => _pendingImage;
        private set
        {
            _pendingImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPendingImage));
            OnPropertyChanged(nameof(PendingImageSource));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanUseComposerAction));
            NotifyCommands();
        }
    }
    public bool HasPendingImage => _pendingImage is not null;
    public Microsoft.UI.Xaml.Media.ImageSource? PendingImageSource => _pendingImage is null || _features is null ? null
        : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(_features.ImagesRoot, _pendingImage)));

    /// <summary>Shrinks a picture and holds it for the next message. Returns a message for the user if it failed.</summary>
    public async Task<string?> AttachImageAsync(string path)
    {
        if (!SupportsImages || _generating) return "This model can't see pictures.";
        try
        {
            var bytes = await Winference.App.Services.ImagePreparation.PrepareAsync(path);
            var relative = System.IO.Path.Combine(_conversationId.ToString("N"), "sent-" + Guid.NewGuid().ToString("N") + ".jpg");
            var full = System.IO.Path.Combine(_features!.ImagesRoot, relative);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await System.IO.File.WriteAllBytesAsync(full, bytes);
            RemovePendingImage();
            PendingImage = relative;
            return null;
        }
        catch (RuntimeException error) { return error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "The picture couldn't be saved in Winference's data folder.";
        }
    }

    public void RemovePendingImage()
    {
        if (_pendingImage is null) return;
        try { System.IO.File.Delete(System.IO.Path.Combine(_features!.ImagesRoot, _pendingImage)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        PendingImage = null;
    }

    private byte[]? ReadImage(string? relative)
    {
        if (relative is null || _features is null) return null;
        try { return System.IO.File.ReadAllBytes(System.IO.Path.Combine(_features.ImagesRoot, relative)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The microphone shows once a speech add-on is installed.</summary>
    public bool HasSpeechInput => _features?.Media?.ActiveModel(Winference.Core.Media.MediaKind.SpeechInput) is not null;
    public bool HasImageAddon => _features?.Media?.ActiveModel(Winference.Core.Media.MediaKind.Image) is not null;
    public bool CanDictate => HasSpeechInput && !_shuttingDown && !IsDraftReadOnly;
    public bool CanStartImage => HasImageAddon && !_generating && !_shuttingDown;
    public const string ImageCommand = "/image ";

    private void Media_Changed(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(HasSpeechInput));
        OnPropertyChanged(nameof(HasImageAddon));
        OnPropertyChanged(nameof(CanDictate));
        OnPropertyChanged(nameof(CanStartImage));
    }

    /// <summary>Turns a recording into text with the installed speech add-on. Returns the text, or throws a RuntimeException.</summary>
    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken)
    {
        if (_features?.Media is not { } media || media.ActiveModel(Winference.Core.Media.MediaKind.SpeechInput) is not { } model)
            throw new RuntimeException("speech_unavailable", "Get a speech add-on in Settings first.");
        MarkActive();
        return await media.Service.TranscribeAsync(model, wav, cancellationToken);
    }
    public string PersistenceError => _history?.SaveError ?? string.Empty;
    public bool HasPersistenceError => _history?.HasSaveError == true;
    public bool CanChooseConversation => !_generating && !_modeTransitioning && !_shuttingDown;
    public bool CanChooseModel => !_modeTransitioning && !_shuttingDown;
    public ConversationListItem? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (value is null || _restoringConversation || value.Id == _conversationId) return;
            if (!CanChooseConversation)
            {
                // WinUI ignores a source refresh inside its own two-way update.
                _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(SelectedConversation)));
                return;
            }
            var saved = _history?.Workspace.Conversations.FirstOrDefault(item => item.Id == value.Id);
            if (saved is null) return;
            SaveConversation();
            RestoreConversation(saved);
            SaveConversation();
            RefreshConversations();
        }
    }

    public Task LoadAsync() => ConnectAsync();

    public ModelProfile Profile => _profile;
    public ConversationCompatibility? ConversationCompatibility => _conversationProfile.ConversationCompatibility;
    public bool IsReady => _snapshot.State == RuntimeState.Ready && _snapshot.Session is not null && !_cleanupUnconfirmed;
    /// <summary>False when chats are open for reading but no usable model is selected.</summary>
    public bool CanLoadModel => _allowLoad;
    public bool IsShutDown => _disposed;
    public bool IsBusy => _connecting || _generating || _modeTransitioning || _shuttingDown;
    public RuntimeSession? ActiveSession => !_cleanupUnconfirmed && _snapshot.State is RuntimeState.Ready or RuntimeState.Generating
        ? _snapshot.Session : null;
    public string ContextNotice => _contextNotice;
    public bool HasContextNotice => _contextNotice.Length > 0;

    /// <summary>Freeze user operations during preflight without stopping the current engine.</summary>
    public void SetSwitching(bool switching, string? detail = null)
    {
        _modeTransitioning = switching;
        _switchDetail = switching ? detail ?? "Checking the requested model…" : null;
        NotifyStateChanged();
    }

    /// <summary>The transition coordinator needs a failing Task, rather than a ready-looking target.</summary>
    public async Task LoadForSwitchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsReady) return;
        if (!CanConnect) throw new RuntimeException("TargetLoadUnavailable", "The target model cannot load while another operation is active.");
        using var cancel = cancellationToken.Register(() => _connectCancellation?.Cancel());
        await ConnectAsync();
        _snapshot = _broker.Snapshot;
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady || !string.IsNullOrWhiteSpace(_operationError))
            throw new RuntimeException("TargetLoadFailed", _operationError ?? "The target model did not become ready. Your conversation was kept.");
    }

    public void ReportLoading(SetupProgress progress)
    {
        if (!_dispatcher.HasThreadAccess) { _dispatcher.TryEnqueue(() => ReportLoading(progress)); return; }
        _loadingDetail = progress.Message;
        OnPropertyChanged(nameof(ConnectionDetail));
    }

    public void ReportLoadFailure(string message)
    {
        _operationError = message;
        NotifyStateChanged();
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand NewChatCommand { get; }
    public ICommand RetryCommand { get; }
    public bool CanRetryReply => !_shuttingDown && !_shutdownFailed && !_modeTransitioning && !_connecting && !_generating &&
        !_cleanupUnconfirmed &&
        (_snapshot.State == RuntimeState.Ready || CanConnect) && RetryPair() is { } pair && pair.Assistant.PreviousAttempts.Count < 32;
    public string RetryActionHelp => "Retry the last question without adding another user message. The previous reply attempt is kept in saved history.";

    public string DraftMessage
    {
        get => _shell.DraftMessage;
        set => _shell.DraftMessage = value;
    }

    public bool HasConversation => Messages.Count > 0;
    public bool IsConnecting => _connecting;
    public bool IsGenerating => _generating || _snapshot.State == RuntimeState.Generating;
    public bool CanEditDraft => !_generating && !_modeTransitioning && !_shuttingDown;
    public bool IsDraftReadOnly => !CanEditDraft;
    public bool CanStopReply => (_connecting || IsGenerating) && !_shuttingDown;
    public bool CanDisconnect => !_shuttingDown && !_modeTransitioning && !_connecting && !_generating &&
        (_snapshot.State == RuntimeState.Ready || _cleanupUnconfirmed || _shutdownFailed);
    public bool CanConnect => _allowLoad && !_shuttingDown && !_shutdownFailed && !_modeTransitioning && !_connecting && !_generating && !_cleanupUnconfirmed &&
        _snapshot.State is RuntimeState.Idle or RuntimeState.Error;
    public bool CanSend => _allowLoad && !_shuttingDown && !_shutdownFailed && !_generating &&
        (!string.IsNullOrWhiteSpace(DraftMessage) || PendingImage is not null);
    public bool CanNewChat => !_shuttingDown && !_modeTransitioning && (HasConversation || _shell.HasDraft);
    public bool HasNotice => !string.IsNullOrWhiteSpace(_operationError) || _snapshot.State == RuntimeState.Error;
    public bool HasErrorNotice => !string.IsNullOrWhiteSpace(_operationError) || _snapshot.State == RuntimeState.Error;
    public string RuntimeActionLabel => _snapshot.State == RuntimeState.Error || !string.IsNullOrWhiteSpace(_operationError) ? "Retry" : "Load model";
    public bool ShowConnectAction => _allowLoad && !_shuttingDown && !_modeTransitioning && !_connecting && !_generating &&
        _snapshot.State != RuntimeState.Ready && !_cleanupUnconfirmed;
    public bool ShowDisconnectAction => !_shuttingDown && !_modeTransitioning && !_connecting && !_generating &&
        (_snapshot.State == RuntimeState.Ready || _cleanupUnconfirmed);
    public string StopActionName => _connecting ? "Cancel model startup" : "Stop reply";
    // Send stays running for a whole reply, so one button drives two separate commands.
    // While a model loads, typed text means "send when ready"; an empty box means the button can cancel.
    public bool ComposerStops => CanStopReply && !(_connecting && CanSend);
    public bool CanUseComposerAction => CanStopReply || CanSend;
    public string ComposerActionGlyph => ComposerStops ? "\uE71A" : "\uE72A";
    public string ComposerActionName => ComposerStops ? StopActionName : "Send message";
    public bool ShowRetryReply => CanRetryReply && Messages.LastOrDefault()?.AttemptOutcome is "failed" or "interrupted" or "length";

    public string ModelStatus => _snapshot.State is RuntimeState.Ready or RuntimeState.Generating
        ? $"Ready - {_snapshot.Session?.Device ?? "device unverified"}"
        : _availability switch
        {
            { IsAvailable: true } => "Available",
            { IsAvailable: false } => "Unavailable",
            _ => "Not checked"
        };

    public string ConnectionStatus
    {
        get
        {
            if (_modeTransitioning && _switchDetail is not null) return "Switching model…";
            if (!string.IsNullOrWhiteSpace(_operationError)) return "Need attention";
            if (_snapshot.State == RuntimeState.Stopping) return "Stopping AI";
            if (_connecting) return "Warming up…";
            if (_generating) return "Writing reply…";
            if (_snapshot.State == RuntimeState.Ready) return "Ready";
            if (_snapshot.State == RuntimeState.Generating) return "Writing reply…";
            if (_snapshot.State == RuntimeState.Starting) return "Warming up…";
            if (_snapshot.State == RuntimeState.Stopping) return "Stopping AI";
            if (_snapshot.State == RuntimeState.Error) return "Need attention";
            return "Ready";
        }
    }

    public string ConnectionDetail
    {
        get
        {
            if (_modeTransitioning && _switchDetail is not null) return _switchDetail;
            if (!string.IsNullOrWhiteSpace(_operationError)) return _operationError;
            if (_connecting) return _loadingDetail;
            if (_snapshot.Session is { } session)
                return $"{session.Profile.ProviderModelId} - {session.EffectiveContextLength:N0}-token context.";
            if (_snapshot.State != RuntimeState.Idle && !string.IsNullOrWhiteSpace(_snapshot.Detail))
                return _snapshot.Detail;
            if (_availability is not null) return _availability.Detail;
            return "Local model is ready. Send a message to start chatting.";
        }
    }

    /// <summary>
    /// A chat with no model has nothing to stop, but must not stay subscribed once it is
    /// replaced: it would keep saving its older copy of the same conversation.
    /// </summary>
    public void DetachIfUnavailable()
    {
        if (_allowLoad || _disposed) return;
        _disposed = true;
        _broker.Changed -= Broker_Changed;
        _shell.PropertyChanged -= Shell_PropertyChanged;
        if (_features?.Media is { } detachMedia) detachMedia.Changed -= Media_Changed;
        Messages.CollectionChanged -= Messages_CollectionChanged;
        foreach (var message in Messages) message.PropertyChanged -= Message_PropertyChanged;
        if (_history is not null) _history.PropertyChanged -= History_PropertyChanged;
    }

    /// <summary>Call before the window closes; it settles requests and owned processes.</summary>
    public Task ShutdownAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_shutdownTask is not null) return _shutdownTask;
        _shuttingDown = true;
        _connectCancellation?.Cancel();
        _generationCancellation?.Cancel();
        NotifyStateChanged();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdownTask = completion.Task;
        _ = CompleteShutdownAsync(completion);
        // Idle cleanup can finish synchronously and clear _shutdownTask before
        // this method returns. Always return the task retained by this caller.
        return completion.Task;
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        try
        {
            await ShutdownCoreAsync();
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task ShutdownCoreAsync()
    {
        try
        {
            await _broker.DisposeAsync();
            await AwaitOperationAsync(_connectTask);
            await AwaitOperationAsync(_generationTask);
            SaveConversation();
            if (_history is not null) await _history.FlushAsync();
            _disposed = true;
            _broker.Changed -= Broker_Changed;
            _shell.PropertyChanged -= Shell_PropertyChanged;
            if (_features?.Media is { } shutdownMedia) shutdownMedia.Changed -= Media_Changed;
            Messages.CollectionChanged -= Messages_CollectionChanged;
            foreach (var message in Messages) message.PropertyChanged -= Message_PropertyChanged;
            if (_history is not null) _history.PropertyChanged -= History_PropertyChanged;
            _connectCancellation?.Dispose();
            _generationCancellation?.Dispose();
            _connectCancellation = null;
            _generationCancellation = null;
        }
        catch (Exception error)
        {
            _shuttingDown = false;
            _shutdownFailed = true;
            _cleanupUnconfirmed = true;
            _operationError = SafeError(error);
            throw;
        }
        finally
        {
            _shutdownTask = null;
            if (!_disposed) NotifyStateChanged();
        }
    }

    private async Task ConnectAsync()
    {
        if (!CanConnect) return;
        var selectionRevision = _selectionRevision;
        var cancellation = new CancellationTokenSource();
        _connectCancellation = cancellation;
        _connectTask = ConnectCoreAsync(selectionRevision, cancellation.Token);
        await _connectTask;
    }

    private async Task ConnectCoreAsync(long selectionRevision, CancellationToken cancellationToken)
    {
        _connecting = true;
        _operationError = null;
        NotifyStateChanged();

        try
        {
            if (_activateRuntime is not null)
            {
                await _activateRuntime(cancellationToken);
                _availability = new(true, $"The selected model is ready on {_broker.Snapshot.Session?.Device ?? "its selected device"}.");
            }
            else
            {
                var availability = await _broker.DiscoverAsync(_profile, cancellationToken);
                if (!IsCurrentSelection(selectionRevision, cancellationToken)) return;
                _availability = availability;
                if (!availability.IsAvailable)
                {
                    _operationError = availability.Detail;
                    return;
                }
                await _broker.StartAsync(_profile, cancellationToken);
            }
            if (!IsCurrentSelection(selectionRevision, cancellationToken))
            {
                await _broker.StopAsync();
                return;
            }

            _operationError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Mode changes and close cancel this operation intentionally.
        }
        catch (Exception error)
        {
            _operationError = SafeError(error);
            _cleanupUnconfirmed = error is RuntimeException { Code: "CleanupUnconfirmed" } ||
                _operationError.Contains("cleanup could not be confirmed", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            MarkActive();
            _connecting = false;
            if (_connectCancellation?.Token == cancellationToken)
            {
                _connectCancellation.Dispose();
                _connectCancellation = null;
            }
            NotifyStateChanged();
        }
    }

    private async Task SendAsync()
    {
        if (!CanSend) return;
        MarkActive();
        var prompt = DraftMessage;
        DraftMessage = string.Empty;

        if (prompt.StartsWith(ImageCommand.TrimEnd(), StringComparison.OrdinalIgnoreCase) && HasImageAddon)
        {
            var description = prompt[ImageCommand.TrimEnd().Length..].Trim();
            if (description.Length == 0) { DraftMessage = ImageCommand; return; }
            var cancellation = new CancellationTokenSource();
            _generationCancellation = cancellation;
            _generationTask = CreateImageAsync(_selectionRevision, _conversationId, prompt, description, cancellation.Token);
            await _generationTask;
            return;
        }

        var image = SupportsImages ? _pendingImage : null;
        if (image is not null && string.IsNullOrWhiteSpace(prompt)) prompt = "Describe this picture.";
        if (!IsReady)
        {
            await EnsureLoadedAsync();
            if (!IsReady || !string.IsNullOrWhiteSpace(_operationError))
            {
                DraftMessage = prompt == "Describe this picture." && image is not null ? string.Empty : prompt;
                return;
            }
        }
        if (image is not null) { _pendingImage = null; PendingImage = null; }
        await BeginRequestAsync(prompt, retry: false, image);
    }

    private async Task RetryAsync()
    {
        if (!CanRetryReply || RetryPair() is not { } pair) return;
        if (!IsReady)
        {
            await EnsureLoadedAsync();
            if (!IsReady || !string.IsNullOrWhiteSpace(_operationError)) return;
        }
        await BeginRequestAsync(pair.User.DisplayText, retry: true, SupportsImages ? pair.User.ImageRelativePath : null);
    }

    /// <summary>Lets go of the model once it has sat unused for the timeout. The chat and draft are untouched.</summary>
    public void UnloadIfIdle(TimeSpan? timeout)
    {
        if (!IdleUnloadPolicy.ShouldUnload(_lastActivityUtc, DateTimeOffset.UtcNow, timeout, IsReady, IsBusy || _disposed)) return;
        Observe(DisconnectAsync());
    }

    private void MarkActive() => _lastActivityUtc = DateTimeOffset.UtcNow;

    /// <summary>
    /// The model wakes when someone starts typing, so most of the load is over by the time they
    /// send. A failed load waits for an explicit Retry instead of restarting on every keystroke.
    /// </summary>
    private void WakeForTyping()
    {
        if (_restoringConversation || string.IsNullOrWhiteSpace(DraftMessage)) return;
        MarkActive();
        if (CanConnect && _snapshot.State == RuntimeState.Idle && string.IsNullOrWhiteSpace(_operationError))
            Observe(ConnectAsync());
    }

    /// <summary>Joins a load already running, such as the reload after Stop, instead of giving up on it.</summary>
    private async Task EnsureLoadedAsync()
    {
        if (_connecting) await AwaitOperationAsync(_connectTask);
        else await ConnectAsync();
        _snapshot = _broker.Snapshot;
    }

    /// <summary>
    /// A cancelled reply takes the runtime down, because it cannot prove an abandoned request
    /// has stopped. Reloading straight away keeps Stop meaning "end this reply".
    /// </summary>
    private void ReloadAfterStop()
    {
        _snapshot = _broker.Snapshot;
        NotifyStateChanged();
        if (_allowLoad && !_disposed && !_shuttingDown && !_modeTransitioning) Observe(ConnectAsync());
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception error) { HandleCommandFailure(error); }
    }

    private (ChatMessageViewModel User, ChatMessageViewModel Assistant)? RetryPair() =>
        Messages.Count >= 2 && Messages[^2].IsUser && Messages[^1].IsAssistant
            ? (Messages[^2], Messages[^1]) : null;

    private async Task BeginRequestAsync(string prompt, bool retry, string? image = null)
    {
        var cancellation = new CancellationTokenSource();
        _generationCancellation = cancellation;
        _generationTask = SendCoreAsync(_selectionRevision, _conversationId, Guid.NewGuid(), prompt, retry, cancellation.Token, image);
        await _generationTask;
    }

    private IReadOnlyList<ChatMessage> CompletedHistory(int beforeIndex)
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i + 1 < beforeIndex; i++)
        {
            var user = Messages[i];
            var assistant = Messages[i + 1];
            if (!user.IsUser || !assistant.IsAssistant) continue;
            // An unanswered tool request in history invites the model to invent its result.
            if (user.IsContextEligible && assistant.IsContextEligible &&
                !string.IsNullOrWhiteSpace(user.Text) && !string.IsNullOrWhiteSpace(assistant.Text) &&
                !ToolExecutionBroker.LooksLikeToolCall(assistant.Text))
            {
                // Earlier pictures aren't sent again; their messages keep only their words.
                history.Add(new("user", user.DisplayText));
                history.Add(new("assistant", assistant.DisplayText));
            }
            i++;
        }
        return history;
    }

    private ChatMessageViewModel NewAssistant(Guid turnId, Guid attemptId, IReadOnlyList<SavedChatAttempt>? previous = null) =>
        new(ChatSpeaker.Assistant)
        {
            TurnId = turnId, AttemptId = attemptId, AttemptOutcome = "running", Status = "Thinking…",
            OriginModelId = _modelId, OriginProfileId = _profile.ProfileId, OriginArtifactId = _profile.ArtifactId,
            OriginModelName = _shell.ModelName, OriginMode = _profile.ExecutionMode,
            PreviousAttempts = previous ?? []
        };

    private async Task SendCoreAsync(long selectionRevision, Guid conversationId, Guid attemptId,
        string prompt, bool retry, CancellationToken cancellationToken, string? image = null)
    {
        _generating = true;
        _activeAttemptId = attemptId;
        _operationError = null;
        NotifyStateChanged();
        GenerationEvent? terminal = null;
        ChatMessageViewModel? userTurn = null;
        ChatMessageViewModel? assistantTurn = null;
        bool Current() => selectionRevision == _selectionRevision && conversationId == _conversationId &&
            _activeAttemptId == attemptId && !_shuttingDown;

        try
        {
            var pair = retry ? RetryPair() : null;
            if (retry && pair is null) return;
            var toolsOn = _features is not null && _features.ToolsEnabled() && _features.Tools.RegisteredTools.Count > 0;
            IReadOnlyList<ChatMessage> history = CompletedHistory(retry ? Messages.Count - 2 : Messages.Count);
            if (await BuildSystemPromptAsync(prompt, toolsOn, cancellationToken) is { } system)
                history = (ChatMessage[])[new("system", system), .. history];
            var imageBytes = ReadImage(image);
            if (image is not null && imageBytes is null)
                throw new RuntimeException("InvalidImage", "The picture for this message is missing. Attach it again.");
            var imageTokens = imageBytes is null ? 0 : ChatImage.TokenBudget;
            var context = await ContextWindowPolicy.SelectAsync(history, prompt, _profile.ContextLength,
                _profile.MaxOutputTokens, async (messages, token) =>
                {
                    var count = await _broker.CountPromptTokensAsync(messages, token);
                    return imageTokens == 0 ? count : new(count.Tokens + imageTokens, false);
                }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Current()) return;
            _contextNotice = context.ExcludedExchanges > 0
                ? $"{context.ExcludedExchanges} earlier exchange(s) are outside this reply's context. The full conversation is saved."
                : string.Empty;
            if (!context.IsExact)
                _contextNotice += (_contextNotice.Length > 0 ? " " : string.Empty) +
                    "This model uses a conservative context estimate.";
            if (_documentNotice.Length > 0)
                _contextNotice += (_contextNotice.Length > 0 ? " " : string.Empty) + _documentNotice;
            OnPropertyChanged(nameof(ContextNotice));
            OnPropertyChanged(nameof(HasContextNotice));

            if (pair is { } previous)
            {
                userTurn = previous.User;
                var old = previous.Assistant;
                var archived = new SavedChatAttempt(old.AttemptId ?? old.Id, old.Text, old.Status,
                    old.AttemptOutcome ?? (old.IsContextEligible ? "completed" : "interrupted"),
                    old.OriginModelId, old.OriginProfileId, old.OriginArtifactId, old.OriginModelName, old.OriginMode);
                assistantTurn = NewAssistant(userTurn.Id, attemptId, old.PreviousAttempts.Append(archived).ToArray());
                userTurn.IsContextEligible = false;
                Messages[^1] = assistantTurn;
            }
            else
            {
                var turnId = Guid.NewGuid();
                var shown = image is null ? prompt : ChatMessageViewModel.ImageMarker(image) + "\n\n" + prompt;
                userTurn = new(ChatSpeaker.User, shown) { Id = turnId, TurnId = turnId };
                assistantTurn = NewAssistant(turnId, attemptId);
                Messages.Add(userTurn);
                Messages.Add(assistantTurn);
            }

            var messages = context.Messages.ToList();
            if (imageBytes is not null) messages[^1] = messages[^1] with { Images = (ChatImage[])[new ChatImage("image/jpeg", imageBytes)] };
            var historyStart = messages.Count > 0 && messages[0].Role == "system" ? 1 : 0;
            var historyPairs = (messages.Count - historyStart - 1) / 2;
            var toolNotes = string.Empty;
            var unreadableTool = false;
            for (var round = 0; ; ++round)
            {
                terminal = null;
                var reply = new System.Text.StringBuilder();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                TimeSpan? firstToken = null;
                var deltas = 0;
                GenerationUsage? usage = null;
                var temperature = _features?.Temperature() ?? ResponseStyles.DefaultTemperature;
                await foreach (var item in _broker.GenerateAsync(messages, _profile.MaxOutputTokens, cancellationToken, temperature))
                {
                    if (!Current()) break;
                    switch (item.Kind)
                    {
                        case GenerationEventKind.TextDelta:
                            assistantTurn.Status = string.Empty;
                            firstToken ??= clock.Elapsed;
                            deltas++;
                            reply.Append(item.Text);
                            assistantTurn.Text += item.Text ?? string.Empty;
                            break;
                        case GenerationEventKind.Usage:
                            usage = item.Usage;
                            break;
                        case GenerationEventKind.Completed:
                        case GenerationEventKind.Cancelled:
                        case GenerationEventKind.Failed:
                            terminal = item;
                            break;
                    }
                    if (terminal is not null) break;
                }

                if (!Current()) return;
                if (firstToken is { } first && terminal?.Kind == GenerationEventKind.Completed)
                    assistantTurn.Stats = ReplyStats.Describe(usage, deltas, clock.Elapsed - first, first, _broker.Snapshot.Session);
                // A tool request ends this round. Its result goes back to the model as the next message.
                if (!toolsOn || terminal?.Kind != GenerationEventKind.Completed) break;
                var call = round < MaxToolRounds ? _features!.Tools.ParseToolCall(reply.ToString()) : null;
                if (call is null)
                {
                    // A request that can't be run must not look like an answer, or later turns build on it.
                    unreadableTool = ToolExecutionBroker.LooksLikeToolCall(reply.ToString());
                    break;
                }
                var outcome = await RunToolAsync(call, assistantTurn, cancellationToken);
                if (!Current()) return;
                toolNotes += outcome.Note + "\n\n";
                assistantTurn.Text = toolNotes;
                assistantTurn.Status = "Thinking…";
                messages.Add(new("assistant", reply.ToString().Trim()));
                messages.Add(new("user", outcome.Message));
                // Older exchanges make room for the tool result before giving up.
                while (!await FitsContextAsync(messages, cancellationToken))
                {
                    if (historyPairs == 0)
                        throw new RuntimeException("context_exceeded", "The tool result is too long for this model. Try a smaller file or folder.");
                    messages.RemoveRange(historyStart, 2);
                    --historyPairs;
                }
            }
            if (unreadableTool)
            {
                assistantTurn.Text = toolNotes + "*The model tried to use a tool but wrote the request in a way Winference couldn't follow, so nothing ran.*";
                assistantTurn.AttemptOutcome = "failed";
                assistantTurn.Status = "Tool request not understood. Retry, or try a larger model.";
                _operationError = null;
                if (!retry && string.Equals(DraftMessage, prompt, StringComparison.Ordinal)) DraftMessage = string.Empty;
            }
            else if (terminal?.Kind == GenerationEventKind.Completed && terminal.FinishReason != "length" &&
                !string.IsNullOrWhiteSpace(assistantTurn.Text))
            {
                userTurn.IsContextEligible = true;
                assistantTurn.IsContextEligible = true;
                assistantTurn.AttemptOutcome = "completed";
                assistantTurn.Status = string.Empty;
                if (!retry && string.Equals(DraftMessage, prompt, StringComparison.Ordinal)) DraftMessage = string.Empty;
                _operationError = null;
            }
            else if (terminal?.Kind == GenerationEventKind.Cancelled || cancellationToken.IsCancellationRequested)
            {
                assistantTurn.AttemptOutcome = "stopped";
                assistantTurn.Status = "Stopped";
                _operationError = null;
            }
            else if (terminal?.Kind == GenerationEventKind.Completed && terminal.FinishReason == "length")
            {
                assistantTurn.AttemptOutcome = "length";
                assistantTurn.Status = "Reply limit reached";
                _operationError = null;
                _contextNotice += (_contextNotice.Length > 0 ? " " : string.Empty) +
                    "The reply reached its length limit. You can ask a follow-up or retry.";
                OnPropertyChanged(nameof(ContextNotice));
                OnPropertyChanged(nameof(HasContextNotice));
                if (!retry && string.Equals(DraftMessage, prompt, StringComparison.Ordinal)) DraftMessage = string.Empty;
            }
            else
            {
                assistantTurn.AttemptOutcome = "failed";
                assistantTurn.Status = "Interrupted";
                _operationError = terminal?.Error ?? "The reply ended before it completed. Try loading the model again.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (assistantTurn is not null)
            {
                assistantTurn.AttemptOutcome = "stopped";
                assistantTurn.Status = "Stopped";
            }
        }
        catch (Exception error)
        {
            if (assistantTurn is not null)
            {
                assistantTurn.AttemptOutcome = "failed";
                assistantTurn.Status = "Interrupted";
            }
            if (Current()) _operationError = SafeError(error);
        }
        finally
        {
            if (assistantTurn is { AttemptOutcome: "running" })
            {
                assistantTurn.AttemptOutcome = "interrupted";
                assistantTurn.Status = "Interrupted";
            }
            if (_activeAttemptId == attemptId)
            {
                MarkActive();
                _activeAttemptId = null;
                _generating = false;
                SaveConversation();
                RefreshConversations();
                if (_generationCancellation is not null)
                {
                    _generationCancellation.Dispose();
                    _generationCancellation = null;
                }
                NotifyStateChanged();
            }
        }
    }
}
