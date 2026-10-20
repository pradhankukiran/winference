using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winference.Core.Models;
using Winference.Core.Setup;

namespace Winference.App.ViewModels;

/// <summary>
/// Presentation state for the native shell. The processor shown here always comes
/// from the selected model; each runtime needs its own model files.
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private string _draftMessage = string.Empty;
    private string? _shellError;
    private string? _modelActivity;
    private string? _downloadActivity;
    private double? _downloadPercent;
    private string? _offeredModel;
    private ChatViewModel? _chat;
    private SetupModel? _selectedModel;
    private ModelProfile? _profile;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ExecutionMode? ActiveMode => _chat?.ActiveSession?.Profile.ExecutionMode;

    public const string NoModelName = "Choose a model";
    public string ModelName => _selectedModel is { DisplayName: not NoModelName } model ? ModelNames.Clean(model.DisplayName) : NoModelName;
    public string ModelPublisher => _selectedModel?.Publisher ?? string.Empty;
    public string ModelStatus => _chat?.ModelStatus ?? "Not checked";
    public int ContextLength => _profile?.ContextLength ?? 0;
    public string ContextLabel => ContextLength > 0 ? $"{ContextLength:N0} tokens" : "Not selected";
    public string ConnectionStatus => _chat?.ConnectionStatus ?? "AI not connected";
    public string ConnectionLabel => ConnectionStatus.Split(new[] { " - ", " · " }, 2, StringSplitOptions.None)[0];
    public string ConnectionDetail => _chat?.ConnectionDetail ?? "Model installation and device availability have not been checked.";
    public bool CanSend => _chat?.CanSend ?? false;
    public ChatViewModel? Chat => _chat;
    public string? ActiveModelId => _chat is { CanLoadModel: false } ? null : _selectedModel?.Id;

    public string HardwareBadgeLabel => _chat is { CanLoadModel: false } ? "No model" : (ActiveMode ?? SelectedMode) switch
    {
        ExecutionMode.NpuOnly => "NPU",
        ExecutionMode.GpuOnly => "GPU",
        ExecutionMode.GpuNpu => "GPU + NPU",
        _ => "Local AI"
    };

    public string FriendlyStatus => _chat switch
    {
        { CanLoadModel: false } => "Choose a model",
        { IsGenerating: true } => "Thinking…",
        { IsConnecting: true } => "Loading…",
        { HasErrorNotice: true } => "Needs attention",
        { IsReady: true } => "Ready",
        _ => "Not loaded"
    };

    public bool HasDraft => _draftMessage.Length > 0;
    public ExecutionMode SelectedMode => _profile?.ExecutionMode ?? ExecutionMode.NpuOnly;

    public void SelectModel(SetupModel model, ModelProfile profile)
    {
        _selectedModel = model;
        _profile = profile;
        foreach (var name in new[] { nameof(ModelName), nameof(ModelPublisher), nameof(ContextLength), nameof(ContextLabel),
            nameof(SelectedMode), nameof(HardwareBadgeLabel) })
            OnPropertyChanged(name);
    }

    public void AttachChat(ChatViewModel chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (ReferenceEquals(_chat, chat)) return;
        if (_chat is not null) _chat.PropertyChanged -= Chat_PropertyChanged;
        _chat = chat;
        _chat.PropertyChanged += Chat_PropertyChanged;
        OnPropertyChanged(nameof(Chat));
        OnPropertyChanged(nameof(ModelStatus));
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(ConnectionDetail));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ActiveMode));
        OnPropertyChanged(nameof(HardwareBadgeLabel));
        OnPropertyChanged(nameof(FriendlyStatus));
    }

    private void Chat_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ChatViewModel.ModelStatus)) OnPropertyChanged(nameof(ModelStatus));
        if (args.PropertyName is nameof(ChatViewModel.ConnectionStatus))
        {
            OnPropertyChanged(nameof(ConnectionStatus));
            OnPropertyChanged(nameof(ConnectionLabel));
            OnPropertyChanged(nameof(ActiveMode));
            OnPropertyChanged(nameof(HardwareBadgeLabel));
            OnPropertyChanged(nameof(FriendlyStatus));
        }
        if (args.PropertyName is nameof(ChatViewModel.ConnectionDetail)) OnPropertyChanged(nameof(ConnectionDetail));
        if (args.PropertyName is nameof(ChatViewModel.CanSend)) OnPropertyChanged(nameof(CanSend));
    }
}
