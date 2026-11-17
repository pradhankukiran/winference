using Winference.Core.Chat;
using Winference.Core.Runtime;

namespace Winference.Core.Boards;

/// <summary>What a question sends to the model: the path of questions and answers that leads to it.</summary>
public static class BoardContext
{
    /// <summary>
    /// Earlier exchanges on the way to <paramref name="questionId"/>, oldest first. Only answers that
    /// finished are included, so a failed or stopped attempt never becomes the model's memory.
    /// </summary>
    public static IReadOnlyList<ChatMessage> HistoryFor(Board board, Guid questionId)
    {
        var question = board.Find(questionId) ?? throw new ArgumentException("The question isn't on this board.", nameof(questionId));
        var exchanges = new List<(string Question, string Answer)>();
        var parent = question.ParentId is { } answerId ? board.Find(answerId) : null;
        while (parent is { Kind: BoardCardKind.Answer } answer && answer.ParentId is { } askedId && board.Find(askedId) is { IsQuestion: true } asked)
        {
            if (answer.IsUsableAnswer && !string.IsNullOrWhiteSpace(asked.Text)) exchanges.Add((asked.Text, answer.Text));
            parent = asked.ParentId is { } earlier ? board.Find(earlier) : null;
        }
        exchanges.Reverse();
        var messages = new List<ChatMessage>();
        foreach (var (asked, answered) in exchanges)
        {
            messages.Add(new("user", asked));
            messages.Add(new("assistant", answered));
        }
        return messages;
    }

    /// <summary>A board's first question names it, like a chat's first message.</summary>
    public static string TitleFrom(string question)
    {
        var line = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0) return Board.UntitledTitle;
        return line.Length <= 48 ? line : line[..47].TrimEnd() + "…";
    }

    public static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>
/// Places cards as a tree that grows downwards: answers sit side by side under their question,
/// and follow-ups under the answer they continue. Separate threads sit next to each other.
/// </summary>
public static class BoardLayout
{
    public const double CardWidth = 320;
    public const double ColumnGap = 32;
    public const double RowGap = 48;
    public const double DefaultHeight = 120;

    /// <summary>Lays out the whole board. <paramref name="heightOf"/> gives each card's measured height.</summary>
    public static IReadOnlyDictionary<Guid, (double X, double Y)> Tidy(Board board, Func<Guid, double?>? heightOf = null)
    {
        var positions = new Dictionary<Guid, (double X, double Y)>();
        var children = board.Cards.Where(card => card.ParentId is not null)
            .GroupBy(card => card.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(card => card.CreatedUtc).Select(card => card.Id).ToList());
        var known = board.Cards.Select(card => card.Id).ToHashSet();
        var roots = board.Cards.Where(card => card.ParentId is null || !known.Contains(card.ParentId.Value))
            .OrderBy(card => card.CreatedUtc).Select(card => card.Id).ToList();

        var widths = new Dictionary<Guid, double>();
        double Width(Guid id)
        {
            if (widths.TryGetValue(id, out var cached)) return cached;
            var below = children.GetValueOrDefault(id);
            var width = below is null || below.Count == 0 ? CardWidth
                : Math.Max(CardWidth, below.Sum(Width) + ColumnGap * (below.Count - 1));
            widths[id] = width;
            return width;
        }

        void Place(Guid id, double left, double top)
        {
            var width = Width(id);
            positions[id] = (left + (width - CardWidth) / 2, top);
            var below = children.GetValueOrDefault(id);
            if (below is null) return;
            var next = top + (heightOf?.Invoke(id) ?? DefaultHeight) + RowGap;
            var x = left + (width - (below.Sum(Width) + ColumnGap * (below.Count - 1))) / 2;
            foreach (var child in below)
            {
                Place(child, x, next);
                x += Width(child) + ColumnGap;
            }
        }

        var cursor = 0d;
        foreach (var root in roots)
        {
            Place(root, cursor, 0);
            cursor += Width(root) + ColumnGap * 2;
        }
        return positions;
    }

    /// <summary>Where a new card goes without moving the others: beside its siblings, or below everything if it starts a thread.</summary>
    public static (double X, double Y) PlaceNew(Board board, Guid? parentId, Func<Guid, double?>? heightOf = null)
    {
        double Bottom(BoardCard card) => card.Y + (heightOf?.Invoke(card.Id) ?? DefaultHeight);
        if (parentId is { } id && board.Find(id) is { } parent)
        {
            var siblings = board.ChildrenOf(id).ToList();
            var top = Bottom(parent) + RowGap;
            if (siblings.Count == 0) return (parent.X, top);
            // Beside the rightmost card already on that row, so a new answer never covers an old one.
            var row = board.Cards.Where(card => card.Y < top + DefaultHeight && Bottom(card) > top).ToList();
            var right = Math.Max(siblings.Max(card => card.X), row.Count == 0 ? double.MinValue : row.Max(card => card.X));
            return (right + CardWidth + ColumnGap, siblings.Min(card => card.Y));
        }
        return board.Cards.Count == 0 ? (0, 0) : (board.Cards.Min(card => card.X), board.Cards.Max(Bottom) + RowGap * 2);
    }
}

/// <summary>Turns saved chats into boards, once, so nobody loses a conversation when chats become boards.</summary>
public static class BoardImport
{
    public static IReadOnlyList<Board> FromChats(ChatWorkspace chats)
    {
        var boards = new List<Board>();
        foreach (var chat in chats.Conversations.OrderBy(chat => chat.UpdatedUtc))
        {
            var cards = new List<BoardCard>();
            var created = chat.UpdatedUtc.AddMinutes(-chat.Messages.Count);
            Guid? parent = null;
            for (var i = 0; i < chat.Messages.Count; i++)
            {
                var message = chat.Messages[i];
                if (message.Role != "user" || string.IsNullOrWhiteSpace(message.Text)) continue;
                var question = new BoardCard(Guid.NewGuid(), BoardCardKind.Question, parent, message.Text, 0, 0, created = created.AddSeconds(1));
                cards.Add(question);
                parent = null;
                if (i + 1 < chat.Messages.Count && chat.Messages[i + 1] is { Role: "assistant" } reply && !string.IsNullOrWhiteSpace(reply.Text))
                {
                    var completed = reply.IsContextEligible;
                    var answer = new BoardCard(Guid.NewGuid(), BoardCardKind.Answer, question.Id, reply.Text, 0, 0, created = created.AddSeconds(1),
                        Outcome: completed ? AnswerOutcome.Completed : AnswerOutcome.Stopped,
                        ModelId: reply.OriginModelId, ModelName: reply.OriginModelName,
                        Chip: reply.OriginMode is { } mode ? BoardCard.ChipLabel(mode) : null);
                    cards.Add(answer);
                    parent = answer.Id;
                    i++;
                }
            }
            if (cards.Count == 0) continue;
            var board = new Board(chat.Id, chat.Title, chat.UpdatedUtc, cards, chat.HasCustomTitle);
            var layout = BoardLayout.Tidy(board);
            boards.Add(board with { Cards = [.. cards.Select(card => card with { X = layout[card.Id].X, Y = layout[card.Id].Y })] });
        }
        return boards;
    }
}

/// <summary>Starts a reply on a broker that may be busy with another one, waiting for a turn.</summary>
public static class BrokerQueue
{
    /// <summary>
    /// Starts the reply and reads its first event, so problems found before any text (a busy model,
    /// a message the engine refuses) surface as exceptions. Throws "Busy" after <paramref name="busyWait"/>.
    /// </summary>
    public static async Task<IAsyncEnumerator<GenerationEvent>> OpenAsync(RuntimeBroker broker, IReadOnlyList<ChatMessage> messages,
        int? maxTokens, double temperature, TimeSpan busyWait, CancellationToken cancellationToken)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var stream = broker.GenerateAsync(messages, maxTokens, cancellationToken, temperature).GetAsyncEnumerator(cancellationToken);
            try
            {
                if (await stream.MoveNextAsync().ConfigureAwait(false)) return stream;
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new RuntimeException("InterruptedStream", "The model ended the reply before it started.");
            }
            catch (RuntimeException error) when (error.Code == "Busy" && waited.Elapsed < busyWait)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Counts a prompt, waiting while another reply holds the model.</summary>
    public static async Task<PromptTokenCount> CountAsync(RuntimeBroker broker, IReadOnlyList<ChatMessage> messages, TimeSpan busyWait,
        CancellationToken cancellationToken)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return await broker.CountPromptTokensAsync(messages, cancellationToken).ConfigureAwait(false); }
            catch (RuntimeException error) when (error.Code == "Busy" && waited.Elapsed < busyWait)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The newest earlier exchanges that fit the model, plus the question.</summary>
    public static Task<ContextWindowResult> SelectContextAsync(RuntimeBroker broker, Models.ModelProfile profile,
        IReadOnlyList<ChatMessage> history, string question, TimeSpan busyWait, CancellationToken cancellationToken) =>
        ContextWindowPolicy.SelectAsync(history, question, profile.ContextLength, profile.MaxOutputTokens,
            (messages, token) => CountAsync(broker, messages, busyWait, token), cancellationToken);
}
