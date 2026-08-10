using System.ComponentModel;
using Winference.Core.Models;

namespace Winference.Core.Chat;

/// <summary>One window's local conversations. Writes are serialized and coalesced.</summary>
public sealed class ConversationSession(IConversationStore store) : INotifyPropertyChanged
{
    public const int MaximumTitleLength = 120;
    private ChatWorkspace _workspace = ChatWorkspace.Empty;
    private long _revision;
    private long _savedRevision;
    private Task? _saveTask;
    private string? _error;
    private bool _loadFailed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ChatWorkspace Workspace => _workspace;
    public string? Warning { get; private set; }
    public string SaveError => _error ?? string.Empty;
    public bool HasSaveError => _error is not null;

    /// <summary>
    /// A file that could not be opened is not an empty history. Saving now would replace
    /// real chats, so a failed load leaves the session unable to save until a load succeeds.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            var loaded = await store.LoadAsync();
            _workspace = loaded.Workspace;
            Warning = loaded.Warning;
            _loadFailed = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            SetError("Saved chats could not be opened, so new changes are not being saved. Close the app and open it again. Your saved chats were not changed.");
            throw;
        }
    }

    public SavedConversation Open(string modelId, ModelProfile profile)
    {
        return _workspace.Conversations.FirstOrDefault(item => item.Id == _workspace.ActiveConversationId)
            ?? _workspace.Conversations.OrderByDescending(item => item.UpdatedUtc).FirstOrDefault()
            ?? New(modelId, profile);
    }

    /// <summary>
    /// True when the last reply came from a different model. Builds reviewed as the same
    /// checkpoint do not count: the conversation's identity follows the open chat, so only
    /// the reply's own origin says who wrote it.
    /// </summary>
    public static bool ModelChanged(string? lastReplyModelId, string modelId,
        ConversationCompatibility? conversation, ConversationCompatibility? current) =>
        lastReplyModelId is not null && lastReplyModelId != modelId && (current is null || current != conversation);
}
