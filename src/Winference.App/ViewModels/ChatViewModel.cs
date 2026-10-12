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
}
