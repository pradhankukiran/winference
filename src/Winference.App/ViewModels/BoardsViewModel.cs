using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Winference.App.Diagnostics;
using Winference.Core.Boards;
using Winference.Core.Chat;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Infrastructure.Windows;

namespace Winference.App.ViewModels;

/// <summary>The services a board uses to get answers.</summary>
public sealed record BoardServices(
    ShellViewModel Shell,
    ModelLibraryViewModel Library,
    ChipActivityViewModel Chip,
    ReplyMeter Meter,
    Func<string> Instructions,
    Func<double> Temperature,
    Action<string, double>? RecordSpeed);

/// <summary>All boards: the sidebar list, the open board, and saving.</summary>
public sealed class BoardsViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);
    private readonly IBoardStore _store;
    private readonly BoardServices _services;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly List<BoardViewModel> _all = [];
    private BoardViewModel? _active;
    private string _query = string.Empty;
    private bool _imported;
    private string? _saveError;

    public BoardsViewModel(IBoardStore store, BoardServices services, DispatcherQueue dispatcher)
    {
        _store = store;
        _services = services;
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = SaveDelay;
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Boards shown in the sidebar, newest first, filtered by the search box.</summary>
    public ObservableCollection<BoardViewModel> Boards { get; } = [];

    public BoardViewModel? Active
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value) || value is null) return;
            _active = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            _query = value ?? string.Empty;
            RefreshList();
        }
    }

    public string? SaveError
    {
        get => _saveError;
        private set
        {
            if (_saveError == value) return;
            _saveError = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Opens saved boards. The first time, every saved chat becomes a board, so nothing is lost.</summary>
    public async Task LoadAsync(ChatWorkspace chats)
    {
        BoardWorkspace saved;
        try { saved = await _store.LoadAsync() ?? BoardWorkspace.Empty; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SaveError = "Saved boards couldn't be opened. Check this app's storage access.";
            saved = BoardWorkspace.Empty with { ImportedChats = true };
        }
        var boards = saved.Boards.ToList();
        _imported = saved.ImportedChats;
        if (!_imported)
        {
            var known = boards.Select(board => board.Id).ToHashSet();
            boards.AddRange(BoardImport.FromChats(chats).Where(board => !known.Contains(board.Id)));
            _imported = true;
        }
        foreach (var board in boards) _all.Add(Create(board));
        RefreshList();
        Active = _all.FirstOrDefault(board => board.Id == saved.ActiveBoardId)
            ?? _all.OrderByDescending(board => board.UpdatedUtc).FirstOrDefault()
            ?? NewBoardCore();
        if (!saved.ImportedChats) ScheduleSave();
    }

    /// <summary>Opens an empty board, or reuses the open one if nothing has been asked on it yet.</summary>
    public BoardViewModel NewBoard()
    {
        if (_active is { IsEmpty: true } empty) return empty;
        var board = NewBoardCore();
        Active = board;
        return board;
    }

    public void Rename(BoardViewModel board, string title)
    {
        title = title.Trim();
        if (title.Length == 0) return;
        board.Rename(title);
    }

    public void Delete(BoardViewModel board)
    {
        board.StopWriting();
        _all.Remove(board);
        RefreshList();
        if (ReferenceEquals(_active, board))
        {
            _active = null;
            Active = _all.OrderByDescending(item => item.UpdatedUtc).FirstOrDefault() ?? NewBoardCore();
        }
        ScheduleSave();
    }

    public async Task FlushAsync()
    {
        _saveTimer.Stop();
        await SaveAsync();
    }

    private BoardViewModel NewBoardCore()
    {
        var board = Create(Board.New(DateTimeOffset.UtcNow));
        _all.Add(board);
        RefreshList();
        return board;
    }

    private BoardViewModel Create(Board board)
    {
        var view = new BoardViewModel(board, _services);
        view.Changed += (_, _) =>
        {
            ScheduleSave();
            if (Boards.FirstOrDefault() != view) RefreshList();
        };
        return view;
    }

    private void RefreshList()
    {
        var query = _query.Trim();
        var shown = _all.Where(board => query.Length == 0 || board.Matches(query))
            .OrderByDescending(board => board.UpdatedUtc).ToList();
        // Rebuild in place so the list keeps its selection when nothing moved.
        for (var i = Boards.Count - 1; i >= 0; i--) if (!shown.Contains(Boards[i])) Boards.RemoveAt(i);
        for (var i = 0; i < shown.Count; i++)
        {
            var at = Boards.IndexOf(shown[i]);
            if (at == i) continue;
            if (at >= 0) Boards.Move(at, i);
            else Boards.Insert(i, shown[i]);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        // Empty boards aren't worth keeping, except the one that's open.
        var boards = _all.Where(board => !board.IsEmpty || ReferenceEquals(board, _active)).Select(board => board.ToBoard()).ToList();
        try
        {
            await _store.SaveAsync(new BoardWorkspace(BoardWorkspace.CurrentSchemaVersion, _active?.Id, boards, _imported));
            SaveError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppDiagnostics.Write("boards.save.failed", new { type = error.GetType().Name });
            SaveError = "Boards couldn't be saved. Check free space and this app's storage access.";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>One board: its cards, and asking, comparing and following up on it.</summary>
public sealed class BoardViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan BusyWait = TimeSpan.FromMinutes(2);
    private readonly BoardServices _services;
    private string _title;
    private bool _customTitle;
    private DateTimeOffset _updated;
    private BoardCardViewModel? _followUp;
    private BoardCardViewModel? _writing;
    private CancellationTokenSource? _cancel;

    public BoardViewModel(Board board, BoardServices services)
    {
        _services = services;
        Id = board.Id;
        _title = board.Title;
        _customTitle = board.HasCustomTitle;
        _updated = board.UpdatedUtc;
        foreach (var card in board.Cards) Cards.Add(new BoardCardViewModel(card));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Something worth saving changed.</summary>
    public event EventHandler? Changed;
    /// <summary>A card was added that the page should scroll to.</summary>
    public event EventHandler<BoardCardViewModel>? CardAdded;

    public Guid Id { get; }
    public ObservableCollection<BoardCardViewModel> Cards { get; } = [];
    public string Title => _title;
    public DateTimeOffset UpdatedUtc => _updated;
    public bool IsEmpty => Cards.Count == 0;
    public bool IsWriting => _writing is not null;
    public bool CanAsk => !IsWriting;

    /// <summary>The answer the next question continues. Null starts a new thread.</summary>
    public BoardCardViewModel? FollowUp
    {
        get => _followUp;
        set
        {
            if (value is not null && value.Kind != BoardCardKind.Answer) return;
            _followUp = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FollowUpLabel));
            OnPropertyChanged(nameof(HasFollowUp));
            OnPropertyChanged(nameof(AskPlaceholder));
        }
    }

    public bool HasFollowUp => _followUp is not null;
    public string FollowUpLabel => _followUp is null ? string.Empty : "Following up on: " + Snippet(_followUp.Text, 60);
    public string AskPlaceholder => _followUp is null ? "Ask on this board…" : "Ask a follow-up…";

    public bool Matches(string query) => _title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        Cards.Any(card => card.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    public Board ToBoard() => new(Id, _title, _updated, [.. Cards.Select(card => card.ToCard())], _customTitle);

    public void Rename(string title)
    {
        _title = title.Length > ConversationSession.MaximumTitleLength ? title[..ConversationSession.MaximumTitleLength] : title;
        _customTitle = true;
        OnPropertyChanged(nameof(Title));
        Touch();
    }

    /// <summary>Asks a question, as a follow-up to the chosen answer or as a new thread, then writes its answer.</summary>
    public async Task AskAsync(string text, Func<Guid, double?> heightOf)
    {
        text = text.Trim();
        if (text.Length == 0 || IsWriting) return;
        var parent = _followUp;
        FollowUp = null;
        if (IsEmpty && !_customTitle)
        {
            _title = BoardContext.TitleFrom(text);
            OnPropertyChanged(nameof(Title));
        }
        var question = AddCard(BoardCardKind.Question, parent?.Id, text, heightOf);
        await AnswerAsync(question, heightOf);
    }

    /// <summary>Writes another answer to the same question with the model that's loaded now.</summary>
    public Task AgainAsync(BoardCardViewModel answer, Func<Guid, double?> heightOf) =>
        answer.ParentId is { } id && Cards.FirstOrDefault(card => card.Id == id) is { } question && !IsWriting
            ? AnswerAsync(question, heightOf) : Task.CompletedTask;

    /// <summary>Switches to another model, then answers the same question with it, beside the first answer.</summary>
    public async Task CompareAsync(BoardCardViewModel answer, ModelEntryViewModel model, Func<Guid, double?> heightOf)
    {
        if (IsWriting || answer.ParentId is not { } id || Cards.FirstOrDefault(card => card.Id == id) is not { } question) return;
        if (model.Status != ModelStatus.Current)
        {
            var before = _services.Shell.Chat;
            await _services.Library.PerformAsync(model);
            // The switch was declined or failed: don't answer with the wrong model.
            if (ReferenceEquals(before, _services.Shell.Chat)) return;
        }
        await AnswerAsync(question, heightOf);
    }

    public void ToggleStar(BoardCardViewModel answer)
    {
        if (answer.Kind != BoardCardKind.Answer) return;
        answer.IsStarred = !answer.IsStarred;
        Touch();
    }

    public void Delete(BoardCardViewModel card)
    {
        if (ReferenceEquals(card, _writing)) StopWriting();
        var gone = ToBoard().Subtree(card.Id).ToHashSet();
        foreach (var item in Cards.Where(item => gone.Contains(item.Id)).ToList()) Cards.Remove(item);
        if (_followUp is not null && gone.Contains(_followUp.Id)) FollowUp = null;
        OnPropertyChanged(nameof(IsEmpty));
        Touch();
    }

    public void Move(BoardCardViewModel card, double x, double y)
    {
        card.X = Math.Max(0, x);
        card.Y = Math.Max(0, y);
    }

    /// <summary>Called when a drag ends, so a drag saves once rather than on every step.</summary>
    public void Moved() => Changed?.Invoke(this, EventArgs.Empty);

    public void Tidy(Func<Guid, double?> heightOf)
    {
        var layout = BoardLayout.Tidy(ToBoard(), heightOf);
        foreach (var card in Cards)
            if (layout.TryGetValue(card.Id, out var place)) { card.X = place.X; card.Y = place.Y; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void StopWriting() => _cancel?.Cancel();

    private BoardCardViewModel AddCard(BoardCardKind kind, Guid? parentId, string text, Func<Guid, double?> heightOf)
    {
        var (x, y) = BoardLayout.PlaceNew(ToBoard(), parentId, heightOf);
        var card = new BoardCardViewModel(new BoardCard(Guid.NewGuid(), kind, parentId, text, x, y, DateTimeOffset.UtcNow,
            Outcome: kind == BoardCardKind.Answer ? AnswerOutcome.Writing : AnswerOutcome.None));
        Cards.Add(card);
        OnPropertyChanged(nameof(IsEmpty));
        CardAdded?.Invoke(this, card);
        Touch();
        return card;
    }

    private async Task AnswerAsync(BoardCardViewModel question, Func<Guid, double?> heightOf)
    {
        var answer = AddCard(BoardCardKind.Answer, question.Id, string.Empty, heightOf);
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        _writing = answer;
        WritingChanged();
        var meter = _services.Meter;
        var energy = new EnergyTally();
        void Sampled(object? sender, ChipSample sample) => energy.Add(sample.ChipWatts);
        IAsyncEnumerator<GenerationEvent>? stream = null;
        try
        {
            answer.Status = "Getting the model ready…";
            var chat = await ReadyChatAsync(cancel.Token);
            var profile = chat.Profile;
            answer.ModelId = chat.ModelId;
            answer.ModelName = _services.Shell.ModelName;
            answer.Chip = BoardCard.ChipLabel(profile.ExecutionMode);

            var history = BoardContext.HistoryFor(ToBoard(), question.Id).ToList();
            var instructions = _services.Instructions().Trim();
            if (instructions.Length > 0) history.Insert(0, new ChatMessage("system", instructions));
            var context = await BrokerQueue.SelectContextAsync(chat.Broker, profile, history, question.Text, BusyWait, cancel.Token);
            answer.Status = "Thinking…";
            stream = await BrokerQueue.OpenAsync(chat.Broker, context.Messages, null, _services.Temperature(), BusyWait, cancel.Token);

            meter.Begin();
            _services.Chip.Sampled += Sampled;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan? firstText = null;
            var pieces = 0;
            GenerationUsage? usage = null;
            GenerationEvent? terminal = null;
            do
            {
                var item = stream.Current;
                switch (item.Kind)
                {
                    case GenerationEventKind.TextDelta:
                        firstText ??= clock.Elapsed;
                        ++pieces;
                        meter.Tick();
                        answer.Status = string.Empty;
                        answer.Text += item.Text;
                        break;
                    case GenerationEventKind.ReasoningDelta:
                        meter.Tick();
                        answer.Status = "Thinking…";
                        break;
                    case GenerationEventKind.Usage:
                        usage = item.Usage;
                        break;
                    default:
                        terminal = item;
                        break;
                }
            } while (terminal is null && await stream.MoveNextAsync());

            var writing = firstText is { } start ? clock.Elapsed - start : TimeSpan.Zero;
            if (terminal?.Kind == GenerationEventKind.Completed && !string.IsNullOrWhiteSpace(answer.Text))
            {
                answer.Outcome = terminal.FinishReason == "length" ? AnswerOutcome.CutShort : AnswerOutcome.Completed;
                answer.Status = answer.Outcome == AnswerOutcome.CutShort ? "Reached the reply limit" : string.Empty;
                var words = BoardContext.WordCount(answer.Text);
                if (writing.TotalSeconds > 0.3 && words > 2) answer.WordsPerSecond = words / writing.TotalSeconds;
                answer.Watts = energy.AverageWatts;
                answer.BatteryPercent = energy.AverageWatts is { } watts ? BatteryShare(watts, clock.Elapsed) : null;
                if ((usage?.CompletionTokens ?? pieces) >= 32 && ReplyStats.Rate(usage, pieces, writing) is { } rate)
                    _services.RecordSpeed?.Invoke(chat.ModelId, rate);
            }
            else if (terminal?.Kind == GenerationEventKind.Failed)
                Fail(answer, terminal.Error ?? "The model couldn't finish this answer.");
            else
            {
                answer.Outcome = AnswerOutcome.Stopped;
                answer.Status = "Stopped";
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            answer.Outcome = AnswerOutcome.Stopped;
            answer.Status = "Stopped";
        }
        catch (RuntimeException error) when (error.Code == "Busy")
        {
            Fail(answer, "The model stayed busy with another reply. Try again in a moment.");
        }
        catch (RuntimeException error) { Fail(answer, error.Message); }
        catch (Exception error)
        {
            AppDiagnostics.WriteException("board.answer.failed", error);
            Fail(answer, "Winference couldn't get an answer. Try again.");
        }
        finally
        {
            _services.Chip.Sampled -= Sampled;
            meter.End();
            if (stream is not null) await stream.DisposeAsync();
            if (cancel.IsCancellationRequested) _services.Shell.Chat?.WarmUp();
            _writing = null;
            _cancel = null;
            cancel.Dispose();
            WritingChanged();
            Touch();
        }
    }

    /// <summary>Waits for the selected model to be ready, loading it if needed.</summary>
    private async Task<ChatViewModel> ReadyChatAsync(CancellationToken cancellationToken)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_services.Shell.Chat is not { CanLoadModel: true } chat)
                throw new RuntimeException("model_unavailable", "Choose a model below to get answers.");
            var (state, reason) = await chat.PrepareForAppAsync();
            if (state == AppReadiness.Ready) return chat;
            if (state == AppReadiness.Unavailable)
                throw new RuntimeException("model_unavailable", reason ?? "The model isn't available. Choose one below.");
            if (waited.Elapsed > BusyWait) throw new RuntimeException("Busy", "The model is busy.");
            await Task.Delay(400, cancellationToken);
        }
    }

    private static void Fail(BoardCardViewModel answer, string problem)
    {
        answer.Outcome = AnswerOutcome.Failed;
        answer.Problem = problem;
        answer.Status = problem;
    }

    /// <summary>Energy used, as a share of a full battery. Null on PCs without a battery report.</summary>
    private static double? BatteryShare(double watts, TimeSpan duration)
    {
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report.FullChargeCapacityInMilliwattHours is not { } capacity || capacity <= 0) return null;
            var usedMilliwattHours = watts * duration.TotalHours * 1000;
            return usedMilliwattHours / capacity * 100;
        }
        catch (Exception) { return null; }
    }

    private void WritingChanged()
    {
        OnPropertyChanged(nameof(IsWriting));
        OnPropertyChanged(nameof(CanAsk));
        foreach (var card in Cards) card.BoardIsWriting = IsWriting;
    }

    private void Touch()
    {
        _updated = DateTimeOffset.UtcNow;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Snippet(string text, int length)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= length ? line : line[..(length - 1)] + "…";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    public override string ToString() => _title;

    /// <summary>Averages whole-chip power over the readings taken while an answer was written.</summary>
    private sealed class EnergyTally
    {
        private double _sum;
        private int _count;
        public void Add(double? watts)
        {
            if (watts is not { } value || !double.IsFinite(value)) return;
            _sum += value;
            ++_count;
        }
        public double? AverageWatts => _count == 0 ? null : _sum / _count;
    }
}

/// <summary>One card on the canvas.</summary>
public sealed class BoardCardViewModel : INotifyPropertyChanged
{
    private string _text;
    private double _x;
    private double _y;
    private bool _starred;
    private AnswerOutcome _outcome;
    private string? _problem;
    private string? _modelId;
    private string? _modelName;
    private string? _chip;
    private double? _speed;
    private double? _watts;
    private double? _battery;
    private string _status = string.Empty;
    private bool _boardIsWriting;

    public BoardCardViewModel(BoardCard card)
    {
        Id = card.Id;
        Kind = card.Kind;
        ParentId = card.ParentId;
        CreatedUtc = card.CreatedUtc;
        _text = card.Text;
        _x = card.X;
        _y = card.Y;
        _starred = card.IsStarred;
        _outcome = card.Outcome;
        _problem = card.Problem;
        _modelId = card.ModelId;
        _modelName = card.ModelName;
        _chip = card.Chip;
        _speed = card.WordsPerSecond;
        _watts = card.Watts;
        _battery = card.BatteryPercent;
        _status = card.Outcome switch
        {
            AnswerOutcome.Stopped => "Stopped",
            AnswerOutcome.Failed => card.Problem ?? "Failed",
            AnswerOutcome.CutShort => "Reached the reply limit",
            _ => string.Empty
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }
    public BoardCardKind Kind { get; }
    public Guid? ParentId { get; }
    public DateTimeOffset CreatedUtc { get; }
    public bool IsQuestion => Kind == BoardCardKind.Question;
    public bool IsAnswer => Kind == BoardCardKind.Answer;

    public string Text { get => _text; set => Set(ref _text, value, nameof(Text), nameof(HasText)); }
    public bool HasText => _text.Length > 0;
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public bool IsStarred { get => _starred; set => Set(ref _starred, value, nameof(IsStarred), nameof(StarGlyph), nameof(StarLabel)); }
    public string StarGlyph => _starred ? "" : "";
    public string StarLabel => _starred ? "Unstar this answer" : "Star this answer";
    public AnswerOutcome Outcome { get => _outcome; set => Set(ref _outcome, value, nameof(Outcome), nameof(IsWriting), nameof(ShowActions), nameof(IsProblem), nameof(ShowPlainStatus)); }
    public string? Problem { get => _problem; set => Set(ref _problem, value); }
    public string? ModelId { get => _modelId; set => Set(ref _modelId, value); }
    public string? ModelName { get => _modelName; set => Set(ref _modelName, value, nameof(ModelName), nameof(ModelLine)); }
    public string? Chip { get => _chip; set => Set(ref _chip, value, nameof(Chip), nameof(ModelLine)); }
    public double? WordsPerSecond { get => _speed; set => Set(ref _speed, value, nameof(WordsPerSecond), nameof(StatsLine), nameof(HasStats)); }
    public double? Watts { get => _watts; set => Set(ref _watts, value, nameof(Watts), nameof(StatsLine), nameof(HasStats)); }
    public double? BatteryPercent { get => _battery; set => Set(ref _battery, value, nameof(BatteryPercent), nameof(StatsLine), nameof(HasStats)); }
    public string Status { get => _status; set => Set(ref _status, value, nameof(Status), nameof(HasStatus), nameof(ShowPlainStatus)); }
    public bool HasStatus => _status.Length > 0;
    public bool ShowPlainStatus => HasStatus && !IsProblem;
    public bool IsWriting => _outcome == AnswerOutcome.Writing;
    public bool IsProblem => _outcome == AnswerOutcome.Failed;

    /// <summary>Card actions pause while any answer on the board is being written.</summary>
    public bool BoardIsWriting { get => _boardIsWriting; set => Set(ref _boardIsWriting, value, nameof(BoardIsWriting), nameof(CanAct)); }
    public bool CanAct => !_boardIsWriting;
    public bool ShowActions => IsAnswer && !IsWriting;

    public string ModelLine => string.Join(" · ", new[] { _modelName, _chip }.Where(part => !string.IsNullOrWhiteSpace(part)));
    public bool HasStats => StatsLine.Length > 0;
    public string StatsLine
    {
        get
        {
            var parts = new List<string>();
            if (_speed is { } speed) parts.Add($"{speed:0} words/s");
            if (_watts is { } watts) parts.Add($"{watts:0.0} W");
            if (_battery is { } battery) parts.Add(battery < 0.05 ? "<0.1% battery" : $"{battery:0.0}% battery");
            return string.Join(" · ", parts);
        }
    }

    public string AutomationName => IsQuestion ? "Question: " + _text : $"Answer from {ModelLine}: {_text}";

    public BoardCard ToCard() => new(Id, Kind, ParentId, _text, _x, _y, CreatedUtc, _starred, _outcome, _problem,
        _modelId, _modelName, _chip, _speed, _watts, _battery);

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        foreach (var name in names) PropertyChanged?.Invoke(this, new(name));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}
