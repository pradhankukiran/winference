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
}
