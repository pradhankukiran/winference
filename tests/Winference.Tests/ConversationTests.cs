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
    }
}
