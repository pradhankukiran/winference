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

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stop;
        ActiveOperation? operation = null;
        TaskCompletionSource? completion = null;
        RuntimeSnapshot? stopping = null;
        lock (_sync)
        {
            if (_stopping is not null) stop = _stopping;
            else if (_active is null && _snapshot.State == RuntimeState.Idle && !_cleanupUnconfirmed) return Task.CompletedTask;
            else
            {
                ++_revision;
                operation = _active;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                stop = _stopping = completion.Task;
                stopping = SetSnapshot(RuntimeState.Stopping, "Stopping AI…", null);
            }
        }
        if (completion is not null)
        {
            if (stopping is not null) Publish(stopping);
            operation?.RequestCancellation();
            _ = CompleteStopAsync(operation, completion);
        }
        return cancellationToken.CanBeCanceled ? stop.WaitAsync(cancellationToken) : stop;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposing = true;
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_sync) _disposed = true;
        // The gate remains valid if a caller retained a pending operation. It
        // contains no native resource and is intentionally not disposed early.
    }

    private async Task<GenerationEvent> PumpGenerationAsync(ActiveOperation operation, RuntimeSession session,
        GenerationRequest request, ChannelWriter<GenerationEvent> writer)
    {
        GenerationEvent terminal;
        try
        {
            GenerationEvent? receivedTerminal = null;
            await foreach (var item in backend.GenerateAsync(session, request, operation.Cancellation.Token)
                .WithCancellation(operation.Cancellation.Token).ConfigureAwait(false))
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                if (item.IsTerminal)
                {
                    receivedTerminal = item;
                    break;
                }
                if (!Enum.IsDefined(item.Kind) || (item.Kind == GenerationEventKind.TextDelta && item.Text is null))
                    throw new RuntimeException("InvalidStream", "The model returned an invalid reply. Try loading the model again.");
                await writer.WriteAsync(item, operation.Cancellation.Token).ConfigureAwait(false);
            }
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            terminal = receivedTerminal ?? throw new RuntimeException("InterruptedStream", "The reply ended unexpectedly. Try loading the model again.");
            if (terminal.Kind is GenerationEventKind.Cancelled or GenerationEventKind.Failed)
            {
                var cleanupError = await TryStopBackendAsync().ConfigureAwait(false);
                if (cleanupError is not null) terminal = new(GenerationEventKind.Failed, Error: cleanupError.Message);
            }
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException || operation.Cancellation.IsCancellationRequested;
            var cleanupError = await TryStopBackendAsync().ConfigureAwait(false);
            terminal = cleanupError is not null ? new(GenerationEventKind.Failed, Error: cleanupError.Message)
                : cancelled ? new(GenerationEventKind.Cancelled)
                : new(GenerationEventKind.Failed, Error: SafeMessage(error));
        }
        finally { writer.TryComplete(); }

        RuntimeSnapshot? settled = null;
        lock (_sync)
        {
            if (!IsCurrent(operation) && terminal.Kind == GenerationEventKind.Completed)
                terminal = new(GenerationEventKind.Cancelled);
            if (IsCurrent(operation))
                settled = terminal.Kind switch
                {
                    GenerationEventKind.Completed => SetSnapshot(RuntimeState.Ready, $"Ready · {session.Device}", session),
                    GenerationEventKind.Cancelled => SetSnapshot(RuntimeState.Idle, "AI stopped", null),
                    _ => SetSnapshot(RuntimeState.Error, terminal.Error ?? "The reply was interrupted. Load the model again.", null)
                };
        }
        if (settled is not null) Publish(settled);
        Finish(operation);
        return terminal;
    }

    private async Task CompleteStopAsync(ActiveOperation? operation, TaskCompletionSource completion)
    {
        try
        {
            using var deadline = new CancellationTokenSource(_shutdownTimeout);
            await StopBackendAsync(deadline.Token).ConfigureAwait(false);
            if (operation is not null)
            {
                await operation.Completed.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                // A cancelled start may finish creating a child after the first
                // stop call. Settle it once more after the operation's barrier.
                await StopBackendAsync(deadline.Token).ConfigureAwait(false);
            }
            RuntimeSnapshot stopped;
            lock (_sync)
            {
                stopped = SetSnapshot(RuntimeState.Idle, "AI stopped", null);
                _cleanupUnconfirmed = false;
                _stopping = null;
            }
            Publish(stopped);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            var failure = error as RuntimeException ?? new RuntimeException("CleanupUnconfirmed",
                "AI cleanup could not be confirmed. Close the app and check Diagnostics before loading a model.", error);
            RuntimeSnapshot failed;
            lock (_sync)
            {
                failed = SetSnapshot(RuntimeState.Error, failure.Message, null);
                _cleanupUnconfirmed = true;
                _stopping = null;
            }
            Publish(failed);
            completion.TrySetException(failure);
        }
    }

    private async Task StopBackendAsync(CancellationToken cancellationToken)
    {
        await _cleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? stopping = null;
        try
        {
            stopping = backend.StopAsync(cancellationToken);
            await stopping.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (stopping is { IsCompleted: false }) _ = ReleaseCleanupGateWhenSettledAsync(stopping);
            else _cleanupGate.Release();
        }
    }

    private async Task ReleaseCleanupGateWhenSettledAsync(Task stopping)
    {
        try { await stopping.ConfigureAwait(false); }
        catch (Exception) { /* The original waiter reports cleanup unconfirmed. */ }
        finally { _cleanupGate.Release(); }
    }

    private async Task<RuntimeException?> TryStopBackendAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(_shutdownTimeout);
            await StopBackendAsync(deadline.Token).ConfigureAwait(false);
            lock (_sync) _cleanupUnconfirmed = false;
            return null;
        }
        catch (Exception error)
        {
            lock (_sync) _cleanupUnconfirmed = true;
            return error as RuntimeException ?? new RuntimeException("CleanupUnconfirmed",
                "AI cleanup could not be confirmed. Check Diagnostics before loading a model.", error);
        }
    }

    private void Finish(ActiveOperation operation)
    {
        lock (_sync) if (ReferenceEquals(_active, operation)) _active = null;
        operation.Completed.TrySetResult();
        operation.Cancellation.Dispose();
    }

    private bool IsCurrent(ActiveOperation operation) =>
        ReferenceEquals(_active, operation) && operation.Revision == _revision && _stopping is null;

    private RuntimeSnapshot SetSnapshot(RuntimeState state, string detail, RuntimeSession? session) =>
        _snapshot = new(state, detail, session, _revision);

    private void Publish(RuntimeSnapshot snapshot)
    {
        if (Changed is not { } handlers) return;
        foreach (EventHandler<RuntimeSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch (Exception error) { Debug.WriteLine($"Runtime state subscriber failed: {error.GetType().Name}"); }
        }
    }
}
