using Winference.Core.Chat;
using Winference.Infrastructure.Chat;

namespace Winference.Tests;

public class ConversationSessionTests
{
    private static SavedConversation Conversation(string title, DateTimeOffset? updated = null, params string[] messages) =>
        new(Guid.NewGuid(), "model", "profile", "artifact", title, updated ?? DateTimeOffset.UtcNow, string.Empty,
            messages.Select(text => new SavedChatMessage(Guid.NewGuid(), "user", text, string.Empty, false)).ToArray());

    [Fact]
    public async Task Rapid_changes_are_saved_together_and_nothing_is_lost()
    {
        var store = new MemoryConversationStore();
        var session = new ConversationSession(store);
        var conversation = Conversation("first");
        for (var i = 0; i < 50; i++) session.Update(conversation with { Title = "title " + i });

        await session.FlushAsync();

        Assert.True(store.Saves < 5);
        Assert.Equal("title 49", store.Saved!.Conversations.Single().Title);
    }

    [Fact]
    public async Task A_failed_save_is_reported_and_a_later_save_recovers()
    {
        var store = new MemoryConversationStore { SaveFailure = new IOException("disk full") };
        var session = new ConversationSession(store);
        session.Update(Conversation("first"));

        await Assert.ThrowsAsync<IOException>(session.FlushAsync);
        Assert.True(session.HasSaveError);

        store.SaveFailure = null;
        await session.FlushAsync();
        Assert.False(session.HasSaveError);
        Assert.NotNull(store.Saved);
    }

    private sealed class UnreadableStore : IConversationStore
    {
        public int Saves;
        public Task<ChatWorkspaceLoadResult> LoadAsync(CancellationToken cancellationToken = default) => throw new IOException("locked");
        public Task SaveAsync(ChatWorkspace workspace, CancellationToken cancellationToken = default) { ++Saves; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Chats_that_failed_to_open_are_never_overwritten()
    {
        var store = new UnreadableStore();
        var session = new ConversationSession(store);

        await Assert.ThrowsAsync<IOException>(session.LoadAsync);
        session.Update(Conversation("new empty chat"));
        await Assert.ThrowsAsync<IOException>(session.FlushAsync);

        Assert.Equal(0, store.Saves);
        Assert.True(session.HasSaveError);
    }

    [Fact]
    public void Rename_keeps_the_name_marked_as_chosen_by_the_user()
    {
        var session = new ConversationSession(new MemoryConversationStore());
        var conversation = Conversation("hello");
        session.Update(conversation);

        Assert.True(session.Rename(conversation.Id, "  Trip plans  "));

        var renamed = session.Workspace.Conversations.Single();
        Assert.Equal("Trip plans", renamed.Title);
        Assert.True(renamed.HasCustomTitle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_ignores_a_blank_name(string title)
    {
        var session = new ConversationSession(new MemoryConversationStore());
        var conversation = Conversation("hello");
        session.Update(conversation);

        Assert.False(session.Rename(conversation.Id, title));
        Assert.Equal("hello", session.Workspace.Conversations.Single().Title);
    }

    [Fact]
    public void Deleting_the_open_chat_moves_to_the_most_recent_other_chat()
    {
        var session = new ConversationSession(new MemoryConversationStore());
        var older = Conversation("older", DateTimeOffset.UtcNow.AddDays(-2));
        var newer = Conversation("newer", DateTimeOffset.UtcNow.AddDays(-1));
        var open = Conversation("open");
        session.Update(older);
        session.Update(newer);
        session.Update(open);

        Assert.True(session.Delete(open.Id));

        Assert.Equal(newer.Id, session.Workspace.ActiveConversationId);
        Assert.Equal(2, session.Workspace.Conversations.Count);
    }

    [Fact]
    public void Deleting_the_last_chat_leaves_nothing_active()
    {
        var session = new ConversationSession(new MemoryConversationStore());
        var only = Conversation("only");
        session.Update(only);

        session.Delete(only.Id);

        Assert.Null(session.Workspace.ActiveConversationId);
        Assert.Empty(session.Workspace.Conversations);
    }

    [Fact]
    public void Deleting_a_different_chat_keeps_the_open_one_open()
    {
        var session = new ConversationSession(new MemoryConversationStore());
        var other = Conversation("other");
        var open = Conversation("open");
        session.Update(other);
        session.Update(open);

        session.Delete(other.Id);

        Assert.Equal(open.Id, session.Workspace.ActiveConversationId);
    }

    [Fact]
    public void A_reply_from_another_model_is_flagged_even_if_the_chat_was_relabelled()
    {
        Assert.True(ConversationSession.ModelChanged("gguf:abc", "qwen3:0.6b", null, null));
    }

    [Fact]
    public void A_reply_from_the_same_model_is_not_flagged()
    {
        Assert.False(ConversationSession.ModelChanged("qwen3:0.6b", "qwen3:0.6b", null, null));
    }

    [Fact]
    public void A_chat_with_no_replies_is_not_flagged()
    {
        Assert.False(ConversationSession.ModelChanged(null, "qwen3:0.6b", null, null));
    }

    [Fact]
    public void Builds_reviewed_as_the_same_checkpoint_are_not_flagged()
    {
        var shared = new Core.Models.ConversationCompatibility("checkpoint", "1", "tokenizer", "template");
        Assert.False(ConversationSession.ModelChanged("gguf:abc", "hybrid:def", shared, shared));
    }

    [Fact]
    public void Search_finds_titles_and_message_text_newest_first()
    {
        var session = new ConversationSession(new MemoryConversationStore());
        var title = Conversation("Tea notes", DateTimeOffset.UtcNow.AddDays(-1));
        var body = Conversation("Other", DateTimeOffset.UtcNow, "I love green TEA");
        session.Update(title);
        session.Update(body);
        session.Update(Conversation("Rivers"));

        var found = session.Search("tea");

        Assert.Equal([body.Id, title.Id], found.Select(item => item.Id));
        Assert.Equal(3, session.Search("  ").Count);
    }
}

public class JsonConversationStoreTests
{
    private static SavedConversation Conversation(params SavedChatMessage[] messages) =>
        new(Guid.NewGuid(), "model", "profile", "artifact", "Title", DateTimeOffset.UtcNow, string.Empty, messages);

    private static string WorkspacePath(string root) => Path.Combine(root, "Chat", "workspace.json");
}
