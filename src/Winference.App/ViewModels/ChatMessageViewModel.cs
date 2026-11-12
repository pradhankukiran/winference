using System.ComponentModel;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using Winference.Core.Chat;
using Winference.Core.Models;

namespace Winference.App.ViewModels;

public enum ChatSpeaker
{
    User,
    Assistant
}

/// <summary>A single visible turn in the in-memory conversation.</summary>
public sealed class ChatMessageViewModel(ChatSpeaker speaker, string text = "") : INotifyPropertyChanged
{
    private string _text = text;
    private string _status = string.Empty;
    private bool _isContextEligible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? TurnId { get; init; }
    public Guid? AttemptId { get; init; }
    public string? AttemptOutcome { get; set; }
    public string? OriginModelId { get; init; }
    public string? OriginProfileId { get; init; }
    public string? OriginArtifactId { get; init; }
    public string? OriginModelName { get; init; }
    public ExecutionMode? OriginMode { get; init; }
    public IReadOnlyList<SavedChatAttempt> PreviousAttempts { get; init; } = [];

    public ChatSpeaker Speaker { get; } = speaker;
    public string SpeakerLabel => Speaker == ChatSpeaker.User ? "You" :
        string.IsNullOrWhiteSpace(OriginModelName) ? "Winference" : ModelNames.Clean(OriginModelName) + (OriginMode switch
        {
            ExecutionMode.GpuOnly => " · GPU",
            ExecutionMode.GpuNpu => " · GPU + NPU",
            ExecutionMode.NpuOnly => " · NPU",
            _ => string.Empty
        });
    public bool IsUser => Speaker == ChatSpeaker.User;
    public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    public double ContentMaxWidth => IsUser ? 600 : double.PositiveInfinity;
    public Visibility LabelVisibility => IsUser ? Visibility.Collapsed : Visibility.Visible;
    public bool IsAssistant => Speaker == ChatSpeaker.Assistant;

    /// <summary>Only fully completed turns are sent as history on the next request.</summary>
    public bool IsContextEligible
    {
        get => _isContextEligible;
        set
        {
            if (_isContextEligible == value) return;
            _isContextEligible = value;
            OnPropertyChanged();
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (_text == value) return;
            _text = value;
            _image = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasText));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(ImageSource));
            OnPropertyChanged(nameof(ImageVisibility));
        }
    }

    public bool HasText => _text.Length > 0;

    // Generated images are saved as files and referenced from the message text, so saved chats need no new fields.
    private const string ImagePrefix = "![Generated image](winference-image:";
    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _image;

    /// <summary>Folder that generated images are stored under. Set once at startup.</summary>
    public static string? ImagesRoot { get; set; }

    public static string ImageMarker(string relativePath) => ImagePrefix + relativePath.Replace('\\', '/') + ")";

    /// <summary>The image file referenced by this message, relative to <see cref="ImagesRoot"/>.</summary>
    public string? ImageRelativePath
    {
        get
        {
            if (!_text.StartsWith(ImagePrefix, StringComparison.Ordinal)) return null;
            var end = _text.IndexOf(')', ImagePrefix.Length);
            if (end < 0) return null;
            var relative = _text[ImagePrefix.Length..end];
            return relative.Contains("..") || System.IO.Path.IsPathRooted(relative) ? null : relative;
        }
    }

    /// <summary>The text shown, copied and read aloud: the message without its image reference.</summary>
    public string DisplayText
    {
        get
        {
            if (ImageRelativePath is null) return _text;
            var end = _text.IndexOf(')', ImagePrefix.Length);
            return _text[(end + 1)..].TrimStart('\r', '\n');
        }
    }

    public Microsoft.UI.Xaml.Media.ImageSource? ImageSource
    {
        get
        {
            if (_image is not null) return _image;
            if (ImageRelativePath is not { } relative || ImagesRoot is null) return null;
            var path = System.IO.Path.Combine(ImagesRoot, relative);
            if (!System.IO.File.Exists(path)) return null;
            _image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
            return _image;
        }
    }

    public Visibility ImageVisibility => ImageRelativePath is not null ? Visibility.Visible : Visibility.Collapsed;

    private bool _isSpeaking;
    /// <summary>True while this reply is being read aloud.</summary>
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            _isSpeaking = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeakGlyph));
            OnPropertyChanged(nameof(SpeakLabel));
        }
    }

    public Visibility SpeakVisibility => IsAssistant ? Visibility.Visible : Visibility.Collapsed;

    private bool _isLatestReply;
    private bool _canEdit;
    private string _stats = string.Empty;
    private string _reasoning = string.Empty;

    /// <summary>The newest reply, when it can be regenerated.</summary>
    public bool IsLatestReply
    {
        get => _isLatestReply;
        set { if (_isLatestReply == value) return; _isLatestReply = value; OnPropertyChanged(); OnPropertyChanged(nameof(RegenerateVisibility)); }
    }

    public bool CanEdit
    {
        get => _canEdit;
        set { if (_canEdit == value) return; _canEdit = value; OnPropertyChanged(); OnPropertyChanged(nameof(EditVisibility)); }
    }

    /// <summary>A thinking model's working before its answer. Shown collapsed, for replies written in this session only.</summary>
    public string Reasoning
    {
        get => _reasoning;
        set { if (_reasoning == value) return; _reasoning = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(ReasoningVisibility)); }
    }

    public Visibility ReasoningVisibility => _reasoning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Speed and hardware for this reply. Shown for replies written in this session only.</summary>
    public string Stats
    {
        get => _stats;
        set { if (_stats == value) return; _stats = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(StatsVisibility)); }
    }

    public Visibility RegenerateVisibility => _isLatestReply ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditVisibility => _canEdit ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StatsVisibility => _stats.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string SpeakGlyph => _isSpeaking ? "\uE71A" : "\uE767";
    public string SpeakLabel => _isSpeaking ? "Stop reading" : "Read aloud";

    /// <summary>Describes a real in-progress or terminal result, such as Thinking, Stopped, or Interrupted.</summary>
    public string Status
    {
        get => _status;
        set
        {
            value ??= string.Empty;
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(StatusVisibility));
        }
    }

    public bool HasStatus => _status.Length > 0;
    public Visibility StatusVisibility => HasStatus ? Visibility.Visible : Visibility.Collapsed;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
