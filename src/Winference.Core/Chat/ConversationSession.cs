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

    public static SavedConversation New(string modelId, ModelProfile profile) =>
        new(Guid.NewGuid(), modelId, profile.ProfileId, profile.ArtifactId, "New chat", DateTimeOffset.UtcNow, string.Empty, [], profile.ConversationCompatibility);

    public void Update(SavedConversation conversation)
    {
        var items = _workspace.Conversations.Where(item => item.Id != conversation.Id).Append(conversation).ToArray();
        _workspace = new(ChatWorkspace.CurrentSchemaVersion, conversation.Id, items);
        Changed();
    }

    /// <summary>Saves a name the user chose, which later messages will not overwrite.</summary>
    public bool Rename(Guid id, string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || _workspace.Conversations.All(item => item.Id != id)) return false;
        if (trimmed.Length > MaximumTitleLength) trimmed = trimmed[..MaximumTitleLength];
        var items = _workspace.Conversations
            .Select(item => item.Id == id ? item with { Title = trimmed, HasCustomTitle = true } : item).ToArray();
        _workspace = _workspace with { Conversations = items };
        Changed();
        return true;
    }

    /// <summary>Removes a conversation. If it was open, the most recent remaining one becomes active.</summary>
    public bool Delete(Guid id)
    {
        if (_workspace.Conversations.All(item => item.Id != id)) return false;
        var items = _workspace.Conversations.Where(item => item.Id != id).ToArray();
        var active = _workspace.ActiveConversationId == id
            ? items.OrderByDescending(item => item.UpdatedUtc).Select(item => (Guid?)item.Id).FirstOrDefault()
            : _workspace.ActiveConversationId;
        _workspace = _workspace with { ActiveConversationId = active, Conversations = items };
        Changed();
        return true;
    }

    /// <summary>Newest first. Matches the title or the text of any message.</summary>
    public IReadOnlyList<SavedConversation> Search(string? query)
    {
        var text = query?.Trim() ?? string.Empty;
        return _workspace.Conversations
            .Where(item => text.Length == 0 || item.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                item.Messages.Any(message => message.Text.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(item => item.UpdatedUtc)
            .ToArray();
    }

    public async Task FlushAsync()
    {
        EnsureSave();
        if (_saveTask is { } pending) await pending;
        if (_savedRevision != _revision)
            throw new IOException(SaveError.Length > 0 ? SaveError : "Your latest conversation could not be saved.");
    }

    private void Changed()
    {
        ++_revision;
        EnsureSave();
    }

    private void EnsureSave()
    {
        if (_loadFailed || _saveTask is not null || _savedRevision == _revision) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _saveTask = completion.Task;
        _ = SaveLoopAsync(completion);
    }

    private async Task SaveLoopAsync(TaskCompletionSource completion)
    {
        try
        {
            // Typing and streamed tokens update memory immediately. Avoid one disk write per token.
            await Task.Delay(250);
            while (_savedRevision != _revision)
            {
                var revision = _revision;
                var snapshot = _workspace;
                await store.SaveAsync(snapshot);
                _savedRevision = revision;
                SetError(null);
                if (_savedRevision != _revision) await Task.Delay(250);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetError("Your latest chat changes could not be saved. Check available storage and folder access, then try again.");
        }
        finally
        {
            _saveTask = null;
            completion.TrySetResult();
        }
    }

    private void SetError(string? value)
    {
        if (_error == value) return;
        _error = value;
        PropertyChanged?.Invoke(this, new(nameof(SaveError)));
        PropertyChanged?.Invoke(this, new(nameof(HasSaveError)));
    }
}

public sealed record ConversationListItem
{
    public ConversationListItem(Guid id, string title) { Id = id; Title = title; }
    public Guid Id { get; }
    public string Title { get; }
    public string Identity => Id.ToString("N");
}
