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
    }
}
