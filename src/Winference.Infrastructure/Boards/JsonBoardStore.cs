using System.Text.Json;
using System.Text.Json.Serialization;
using Winference.Core.Boards;

namespace Winference.Infrastructure.Boards;

/// <summary>
/// Boards on this PC, in one JSON file. Saves replace the file atomically. A file that can't
/// be read is kept beside the new one, never overwritten.
/// </summary>
public sealed class JsonBoardStore : IBoardStore
{
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private bool _preserveBeforeSave;

    public JsonBoardStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(Path.GetFullPath(dataRoot), "Boards", "boards.json");
    }

    /// <summary>Set when the saved file couldn't be read. The original is kept.</summary>
    public string? Warning { get; private set; }

    public async Task<BoardWorkspace?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            try
            {
                if (new FileInfo(_path).Length > MaximumFileBytes) throw new InvalidDataException("The boards file is too large.");
                await using var input = File.OpenRead(_path);
                var saved = await JsonSerializer.DeserializeAsync<BoardWorkspace>(input, Options, cancellationToken).ConfigureAwait(false);
                if (saved is null || saved.SchemaVersion != BoardWorkspace.CurrentSchemaVersion || saved.Boards is null)
                    throw new InvalidDataException("The boards file has an unknown format.");
                return Clean(saved);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
            {
                _preserveBeforeSave = true;
                Warning = "Saved boards couldn't be read. The original file was kept; new boards can still be saved.";
                return BoardWorkspace.Empty with { ImportedChats = true };
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(BoardWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, workspace, Options, cancellationToken).ConfigureAwait(false);
                if (output.Length > MaximumFileBytes) throw new InvalidDataException("Boards exceed the supported file size; the previous file was kept.");
                output.Flush(flushToDisk: true);
            }
            if (_preserveBeforeSave && File.Exists(_path))
            {
                File.Copy(_path, Path.Combine(directory, $"boards.preserved-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json"), overwrite: false);
                _preserveBeforeSave = false;
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Drops anything a hand edit could have broken, and settles answers interrupted by a crash.</summary>
    private static BoardWorkspace Clean(BoardWorkspace saved)
    {
        var boards = saved.Boards.Where(board => board is not null && board.Id != Guid.Empty).Select(board =>
        {
            var cards = (board.Cards ?? []).Where(card => card is not null && card.Id != Guid.Empty && double.IsFinite(card.X) && double.IsFinite(card.Y))
                .Select(card => card with
                {
                    Text = card.Text ?? string.Empty,
                    Outcome = card.Outcome == AnswerOutcome.Writing ? AnswerOutcome.Stopped : card.Outcome
                }).ToList();
            return board with { Title = string.IsNullOrWhiteSpace(board.Title) ? Board.UntitledTitle : board.Title, Cards = cards };
        }).ToList();
        var active = boards.Any(board => board.Id == saved.ActiveBoardId) ? saved.ActiveBoardId : null;
        return saved with { Boards = boards, ActiveBoardId = active };
    }
}
