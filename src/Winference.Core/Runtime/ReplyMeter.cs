using System.Diagnostics;

namespace Winference.Core.Runtime;

/// <summary>
/// Live writing speed of the reply in progress, from the chat or an app using the local server.
/// Pieces of text stand in for tokens: engines stream about one token per piece.
/// </summary>
public sealed class ReplyMeter
{
    private readonly object _sync = new();
    private readonly Stopwatch _clock = new();
    private TimeSpan? _first;
    private TimeSpan _last;
    private long _pieces;

    public bool IsActive { get { lock (_sync) return _clock.IsRunning; } }

    public void Begin()
    {
        lock (_sync)
        {
            _pieces = 0;
            _first = null;
            _clock.Restart();
        }
    }

    public void Tick()
    {
        lock (_sync)
        {
            if (!_clock.IsRunning) return;
            _last = _clock.Elapsed;
            _first ??= _last;
            ++_pieces;
        }
    }

    public void End()
    {
        lock (_sync) _clock.Stop();
    }

    /// <summary>Pieces per second since the first one, or null until there is enough to measure.</summary>
    public double? TokensPerSecond
    {
        get
        {
            lock (_sync)
            {
                if (_first is not { } first || _pieces < 3) return null;
                var seconds = (_last - first).TotalSeconds;
                return seconds > 0.2 ? (_pieces - 1) / seconds : null;
            }
        }
    }
}
