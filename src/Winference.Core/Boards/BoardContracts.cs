using Winference.Core.Models;

namespace Winference.Core.Boards;

public enum BoardCardKind { Question, Answer }

/// <summary>How an answer card ended. Only completed answers are used as context for follow-ups.</summary>
public enum AnswerOutcome { None, Writing, Completed, CutShort, Stopped, Failed }

/// <summary>
/// One card on a board. Answers hang under their question; a follow-up question hangs under
/// the answer it continues. Positions are in board units, independent of zoom.
/// </summary>
/// <param name="Chip">Where the answer ran, as shown to the user: "NPU", "GPU" or "GPU + NPU".</param>
/// <param name="WordsPerSecond">Words in the answer divided by the time spent writing them.</param>
/// <param name="Watts">Average whole-chip power while the answer was written.</param>
/// <param name="BatteryPercent">Share of a full battery the answer used, when the PC reports both.</param>
public sealed record BoardCard(
    Guid Id,
    BoardCardKind Kind,
    Guid? ParentId,
    string Text,
    double X,
    double Y,
    DateTimeOffset CreatedUtc,
    bool IsStarred = false,
    AnswerOutcome Outcome = AnswerOutcome.None,
    string? Problem = null,
    string? ModelId = null,
    string? ModelName = null,
    string? Chip = null,
    double? WordsPerSecond = null,
    double? Watts = null,
    double? BatteryPercent = null)
{
    public bool IsQuestion => Kind == BoardCardKind.Question;
    public bool IsUsableAnswer => Kind == BoardCardKind.Answer && Outcome is AnswerOutcome.Completed or AnswerOutcome.CutShort &&
        !string.IsNullOrWhiteSpace(Text);

    public static string ChipLabel(ExecutionMode mode) => mode switch
    {
        ExecutionMode.NpuOnly => "NPU",
        ExecutionMode.GpuOnly => "GPU",
        ExecutionMode.GpuNpu => "GPU + NPU",
        _ => "Local"
    };
}

public sealed record Board(Guid Id, string Title, DateTimeOffset UpdatedUtc, IReadOnlyList<BoardCard> Cards, bool HasCustomTitle = false)
{
    public const string UntitledTitle = "New board";

    public static Board New(DateTimeOffset now) => new(Guid.NewGuid(), UntitledTitle, now, []);

    public BoardCard? Find(Guid id) => Cards.FirstOrDefault(card => card.Id == id);

    public IEnumerable<BoardCard> ChildrenOf(Guid? id) => Cards.Where(card => card.ParentId == id);

    /// <summary>The card and everything that hangs below it.</summary>
    public IReadOnlyList<Guid> Subtree(Guid id)
    {
        var found = new List<Guid> { id };
        for (var i = 0; i < found.Count; i++)
            found.AddRange(Cards.Where(card => card.ParentId == found[i]).Select(card => card.Id));
        return found;
    }
}

/// <param name="ImportedChats">True once saved chats have been turned into boards, so it happens only once.</param>
public sealed record BoardWorkspace(int SchemaVersion, Guid? ActiveBoardId, IReadOnlyList<Board> Boards, bool ImportedChats = false)
{
    public const int CurrentSchemaVersion = 1;
    public static BoardWorkspace Empty => new(CurrentSchemaVersion, null, []);
}

public interface IBoardStore
{
    /// <summary>Null when there is no saved file yet.</summary>
    Task<BoardWorkspace?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(BoardWorkspace workspace, CancellationToken cancellationToken = default);
}
