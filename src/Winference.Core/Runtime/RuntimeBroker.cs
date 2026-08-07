using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Winference.Core.Models;

namespace Winference.Core.Runtime;

/// <summary>
/// Coordinates one owned runtime and one request. Inference is pumped independently
/// of the UI reader so stopping cannot depend on a paused or abandoned enumerator.
/// </summary>
public sealed class RuntimeBroker(IRuntimeBackend backend, TimeSpan? shutdownTimeout = null) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly TimeSpan _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(20);
    private RuntimeSnapshot _snapshot = new(RuntimeState.Idle, "AI not connected", null, 0);
    private ActiveOperation? _active;
    private Task? _stopping;
    private long _revision;
    private bool _disposing;
    private bool _disposed;
    private bool _cleanupUnconfirmed;

    public event EventHandler<RuntimeSnapshot>? Changed;

    public RuntimeSnapshot Snapshot { get { lock (_sync) return _snapshot; } }

    public Task<RuntimeAvailability> DiscoverAsync(ModelProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        lock (_sync) ThrowIfDisposed();
        return backend.DiscoverAsync(profile, cancellationToken);
    }

    public async Task<RuntimeSession> StartAsync(ModelProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        ActiveOperation operation;
        RuntimeSnapshot starting;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_active is not null || _stopping is not null)
                throw Busy();
            if (_cleanupUnconfirmed)
                throw new RuntimeException("CleanupUnconfirmed", "AI cleanup is not confirmed. Close the app before loading a model.");
            if (_snapshot.State == RuntimeState.Ready && _snapshot.Session is { } ready)
            {
                if (ready.Profile != profile)
                    throw new RuntimeException("ProfileMismatch", "Unload the current model before choosing another.");
                return ready;
            }
            operation = new ActiveOperation(++_revision, cancellationToken);
            _active = operation;
            starting = SetSnapshot(RuntimeState.Starting, "Starting AI…", null);
        }
        Publish(starting);

        try
        {
            var session = await backend.StartAsync(profile, operation.Cancellation.Token).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            ValidateSession(session, profile);
            RuntimeSnapshot ready;
            lock (_sync)
            {
                if (!IsCurrent(operation)) throw new OperationCanceledException(operation.Cancellation.Token);
                session = session with { Revision = operation.Revision };
                ready = SetSnapshot(RuntimeState.Ready, $"Ready · {session.Device}", session);
            }
            Publish(ready);
            return session;
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException || operation.Cancellation.IsCancellationRequested;
            var cleanupError = await TryStopBackendAsync().ConfigureAwait(false);
            RuntimeSnapshot? failed = null;
            lock (_sync)
            {
                if (IsCurrent(operation))
                    failed = SetSnapshot(cleanupError is not null || !cancelled ? RuntimeState.Error : RuntimeState.Idle,
                        cleanupError?.Message ?? (cancelled ? "AI stopped" : SafeMessage(error)), null);
            }
            if (failed is not null) Publish(failed);
            if (cleanupError is not null) throw cleanupError;
            if (cancelled) throw new OperationCanceledException("AI startup was cancelled.", error, operation.Cancellation.Token);
            if (error is RuntimeException) throw;
            throw new RuntimeException("StartupFailed", SafeMessage(error), error);
        }
        finally { Finish(operation); }
    }

    /// <summary>
    /// Count against the currently proven session. Counting owns the same mutation
    /// barrier as generation so stop/switch cannot publish a stale count.
    /// A context refusal leaves the loaded session intact and the draft recoverable.
    /// </summary>
    public async Task<PromptTokenCount> CountPromptTokensAsync(IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeSession session;
        ActiveOperation operation;
        ChatMessage[] copied;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_active is not null || _stopping is not null) throw Busy();
            session = _snapshot.Session ?? throw new RuntimeException("NotReady", "Load the model before sending a message.");
            if (_snapshot.State != RuntimeState.Ready)
                throw new RuntimeException("NotReady", "The model is not ready. Load it again before sending a message.");
            copied = ValidateMessages(messages, session.Profile.MaxOutputTokens, session.Profile);
            operation = new ActiveOperation(_revision, cancellationToken);
            _active = operation;
        }
        try
        {
            var count = await backend.CountPromptTokensAsync(session, copied, operation.Cancellation.Token).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
                if (!IsCurrent(operation)) throw new OperationCanceledException(operation.Cancellation.Token);
            if (count is null || count.Tokens <= 0)
                throw new RuntimeException("tokenizer_count", "The model did not return a usable context count. Reload it and retry.");
            return count;
        }
        finally { Finish(operation); }
    }

    public async IAsyncEnumerable<GenerationEvent> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        int? maxOutputTokens = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        double temperature = ResponseStyles.DefaultTemperature)
    {
        if (!double.IsFinite(temperature) || temperature is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(temperature));
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeSession session;
        ActiveOperation operation;
        GenerationRequest request;
        RuntimeSnapshot generating;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_active is not null || _stopping is not null) throw Busy();
            session = _snapshot.Session ?? throw new RuntimeException("NotReady", "Load the model before sending a message.");
            if (_snapshot.State != RuntimeState.Ready)
                throw new RuntimeException("NotReady", "The model is not ready. Load it again before sending a message.");
            var outputLimit = maxOutputTokens ?? session.Profile.MaxOutputTokens;
            var copiedMessages = ValidateMessages(messages, outputLimit, session.Profile);
            request = new GenerationRequest(Guid.NewGuid(), session.Revision, session.Profile.ProfileId, copiedMessages, outputLimit, temperature);
            operation = new ActiveOperation(_revision, cancellationToken);
            _active = operation;
            generating = SetSnapshot(RuntimeState.Generating, "Writing reply…", session);
        }
        Publish(generating);
        var channel = Channel.CreateBounded<GenerationEvent>(new BoundedChannelOptions(64)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        var pump = PumpGenerationAsync(operation, session, request, channel.Writer);
        var terminalRead = false;
        try
        {
            while (await channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    bool deliver;
                    lock (_sync) deliver = operation.Revision == _revision && !operation.Cancellation.IsCancellationRequested;
                    if (deliver) yield return item;
                }
            }
            var terminal = await pump.ConfigureAwait(false);
            lock (_sync)
            {
                if (terminal.Kind == GenerationEventKind.Completed &&
                    (operation.Revision != _revision || operation.Cancellation.IsCancellationRequested))
                    terminal = new(GenerationEventKind.Cancelled);
            }
            terminalRead = true;
            yield return terminal;
        }
        finally
        {
            if (!terminalRead)
            {
                // Breaking an await-foreach is a request to cancel, not permission
                // to leave an inference request running in the background.
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                await pump.ConfigureAwait(false);
            }
        }
    }
}
