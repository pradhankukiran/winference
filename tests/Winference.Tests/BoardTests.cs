using Winference.Core.Boards;
using Winference.Core.Chat;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Infrastructure.Boards;

namespace Winference.Tests;

public sealed class BoardLogicTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static BoardCard Question(Guid? parent, string text, int minute) =>
        new(Guid.NewGuid(), BoardCardKind.Question, parent, text, 0, 0, Start.AddMinutes(minute));

    private static BoardCard Answer(Guid parent, string text, int minute, AnswerOutcome outcome = AnswerOutcome.Completed) =>
        new(Guid.NewGuid(), BoardCardKind.Answer, parent, text, 0, 0, Start.AddMinutes(minute), Outcome: outcome);

    [Fact]
    public void A_follow_up_sends_the_path_that_leads_to_it()
    {
        var q1 = Question(null, "Plan Kyoto", 0);
        var a1 = Answer(q1.Id, "Day 1: Fushimi Inari", 1);
        var other = Answer(q1.Id, "Day 1: Arashiyama", 2);
        var q2 = Question(a1.Id, "Cheaper hotels?", 3);
        var board = new Board(Guid.NewGuid(), "Trip", Start, [q1, a1, other, q2]);

        var history = BoardContext.HistoryFor(board, q2.Id);

        Assert.Equal(["user", "assistant"], history.Select(message => message.Role));
        Assert.Equal("Plan Kyoto", history[0].Content);
        Assert.Equal("Day 1: Fushimi Inari", history[1].Content);
    }

    [Fact]
    public void Unfinished_answers_never_become_context()
    {
        var q1 = Question(null, "First", 0);
        var failed = Answer(q1.Id, "half an ans", 1, AnswerOutcome.Failed);
        var q2 = Question(failed.Id, "Second", 2);
        var board = new Board(Guid.NewGuid(), "B", Start, [q1, failed, q2]);
        Assert.Empty(BoardContext.HistoryFor(board, q2.Id));
    }

    [Fact]
    public void Titles_come_from_the_first_question()
    {
        Assert.Equal("Plan three days in Kyoto", BoardContext.TitleFrom("  Plan three\n days in Kyoto "));
        Assert.EndsWith("…", BoardContext.TitleFrom(new string('a', 80)));
        Assert.Equal(Board.UntitledTitle, BoardContext.TitleFrom("   "));
    }

    [Fact]
    public void Tidy_puts_answers_side_by_side_and_follow_ups_below()
    {
        var q1 = Question(null, "Q1", 0);
        var a1 = Answer(q1.Id, "A1", 1);
        var a2 = Answer(q1.Id, "A2", 2);
        var q2 = Question(a1.Id, "Q2", 3);
        var root2 = Question(null, "Another thread", 4);
        var layout = BoardLayout.Tidy(new Board(Guid.NewGuid(), "B", Start, [q1, a1, a2, q2, root2]));

        Assert.Equal(layout[a1.Id].Y, layout[a2.Id].Y);
        Assert.True(layout[a2.Id].X >= layout[a1.Id].X + BoardLayout.CardWidth);
        Assert.True(layout[a1.Id].Y > layout[q1.Id].Y);
        Assert.True(layout[q2.Id].Y > layout[a1.Id].Y);
        Assert.Equal(0, layout[root2.Id].Y);
        Assert.True(layout[root2.Id].X >= layout[a2.Id].X + BoardLayout.CardWidth);
    }

    [Fact]
    public void New_cards_go_under_their_parent_beside_siblings_or_below_everything()
    {
        var q1 = Question(null, "Q1", 0) with { X = 100, Y = 0 };
        var empty = new Board(Guid.NewGuid(), "B", Start, [q1]);
        var first = BoardLayout.PlaceNew(empty, q1.Id);
        Assert.Equal((100, BoardLayout.DefaultHeight + BoardLayout.RowGap), first);

        var a1 = Answer(q1.Id, "A1", 1) with { X = first.X, Y = first.Y };
        var withAnswer = new Board(Guid.NewGuid(), "B", Start, [q1, a1]);
        var second = BoardLayout.PlaceNew(withAnswer, q1.Id);
        Assert.Equal(a1.Y, second.Y);
        Assert.True(second.X >= a1.X + BoardLayout.CardWidth);

        var thread = BoardLayout.PlaceNew(withAnswer, null);
        Assert.True(thread.Y > a1.Y + BoardLayout.DefaultHeight);
        Assert.Equal((0, 0), BoardLayout.PlaceNew(new Board(Guid.NewGuid(), "B", Start, []), null));
    }

    [Fact]
    public void Saved_chats_become_boards_with_their_turns_linked()
    {
        var chat = new SavedConversation(Guid.NewGuid(), "m", "p", "a", "Kyoto", Start, string.Empty,
        [
            new(Guid.NewGuid(), "user", "Plan Kyoto", string.Empty, true),
            new(Guid.NewGuid(), "assistant", "Day 1...", string.Empty, true, OriginModelName: "Qwen", OriginMode: ExecutionMode.NpuOnly),
            new(Guid.NewGuid(), "user", "Hotels?", string.Empty, true),
            new(Guid.NewGuid(), "assistant", "Try Gion", string.Empty, false),
        ]);
        var emptyChat = new SavedConversation(Guid.NewGuid(), "m", "p", "a", "Empty", Start, string.Empty, []);

        var boards = BoardImport.FromChats(new ChatWorkspace(1, null, [chat, emptyChat]));

        var board = Assert.Single(boards);
        Assert.Equal(chat.Id, board.Id);
        Assert.Equal("Kyoto", board.Title);
        Assert.Equal(4, board.Cards.Count);
        Assert.Equal(board.Cards[1].Id, board.Cards[2].ParentId);
        Assert.Equal("NPU", board.Cards[1].Chip);
        Assert.Equal(AnswerOutcome.Stopped, board.Cards[3].Outcome);
        Assert.True(board.Cards[2].Y > board.Cards[1].Y);
    }

    [Fact]
    public void Deleting_a_card_takes_everything_below_it()
    {
        var q1 = Question(null, "Q1", 0);
        var a1 = Answer(q1.Id, "A1", 1);
        var q2 = Question(a1.Id, "Q2", 2);
        var keep = Question(null, "Other", 3);
        var board = new Board(Guid.NewGuid(), "B", Start, [q1, a1, q2, keep]);
        Assert.Equal([a1.Id, q2.Id], board.Subtree(a1.Id));
    }
}

public sealed class BoardStoreTests
{
    [Fact]
    public async Task Boards_survive_a_restart_and_unfinished_answers_are_marked_stopped()
    {
        using var folder = new TemporaryFolder();
        var question = new BoardCard(Guid.NewGuid(), BoardCardKind.Question, null, "Hi", 10, 20, DateTimeOffset.UtcNow);
        var answer = new BoardCard(Guid.NewGuid(), BoardCardKind.Answer, question.Id, "Hel", 10, 200, DateTimeOffset.UtcNow,
            Outcome: AnswerOutcome.Writing, Watts: 9.5);
        var board = new Board(Guid.NewGuid(), "Greeting", DateTimeOffset.UtcNow, [question, answer]);
        await new JsonBoardStore(folder.Path).SaveAsync(new BoardWorkspace(1, board.Id, [board], ImportedChats: true));

        var loaded = await new JsonBoardStore(folder.Path).LoadAsync();

        Assert.NotNull(loaded);
        Assert.True(loaded!.ImportedChats);
        Assert.Equal(board.Id, loaded.ActiveBoardId);
        var cards = loaded.Boards.Single().Cards;
        Assert.Equal(AnswerOutcome.Stopped, cards[1].Outcome);
        Assert.Equal(9.5, cards[1].Watts);
        Assert.Equal((10d, 20d), (cards[0].X, cards[0].Y));
    }

    [Fact]
    public async Task No_file_means_nothing_saved_yet()
    {
        using var folder = new TemporaryFolder();
        Assert.Null(await new JsonBoardStore(folder.Path).LoadAsync());
    }

    [Fact]
    public async Task An_unreadable_file_is_kept_aside_and_chats_are_not_imported_twice()
    {
        using var folder = new TemporaryFolder();
        var directory = Path.Combine(folder.Path, "Boards");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "boards.json"), "{ broken");
        var store = new JsonBoardStore(folder.Path);

        var loaded = await store.LoadAsync();
        Assert.NotNull(store.Warning);
        Assert.True(loaded!.ImportedChats);
        Assert.Empty(loaded.Boards);

        await store.SaveAsync(loaded);
        Assert.Single(Directory.GetFiles(directory, "boards.preserved-*.json"));
    }
}

public sealed class BrokerQueueTests
{
    [Fact]
    public async Task A_reply_waits_for_the_model_to_be_free()
    {
        var backend = new GatedBackend();
        var broker = new RuntimeBroker(backend, TimeSpan.FromSeconds(5));
        await broker.StartAsync(TestProfiles.Create());
        var first = BrokerQueue.OpenAsync(broker, [new ChatMessage("user", "first")], null, 0.7, TimeSpan.FromSeconds(1), CancellationToken.None);
        await using var firstReply = await first;

        var waiting = BrokerQueue.OpenAsync(broker, [new ChatMessage("user", "second")], null, 0.7, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(400);
        Assert.False(waiting.IsCompleted);

        backend.Release.TrySetResult();
        while (await firstReply.MoveNextAsync() && !firstReply.Current.IsTerminal) { }
        await using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GenerationEventKind.TextDelta, second.Current.Kind);
    }

    [Fact]
    public async Task A_model_that_stays_busy_is_reported()
    {
        var backend = new GatedBackend();
        var broker = new RuntimeBroker(backend, TimeSpan.FromSeconds(5));
        await broker.StartAsync(TestProfiles.Create());
        await using var first = await BrokerQueue.OpenAsync(broker, [new ChatMessage("user", "first")], null, 0.7, TimeSpan.FromSeconds(1), CancellationToken.None);
        var error = await Assert.ThrowsAsync<RuntimeException>(() =>
            BrokerQueue.OpenAsync(broker, [new ChatMessage("user", "second")], null, 0.7, TimeSpan.FromMilliseconds(500), CancellationToken.None));
        Assert.Equal("Busy", error.Code);
        backend.Release.TrySetResult();
    }

    /// <summary>The first reply holds the model until released; later replies finish straight away.</summary>
    private sealed class GatedBackend : IRuntimeBackend
    {
        private int _replies;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeAvailability(true, "ready"));

        public Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(TestProfiles.Session(profile));

        public async IAsyncEnumerable<GenerationEvent> GenerateAsync(RuntimeSession session, GenerationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var first = Interlocked.Increment(ref _replies) == 1;
            yield return new(GenerationEventKind.TextDelta, Text: "hi");
            if (first) await Release.Task.WaitAsync(cancellationToken);
            yield return new(GenerationEventKind.Completed, FinishReason: "stop");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
