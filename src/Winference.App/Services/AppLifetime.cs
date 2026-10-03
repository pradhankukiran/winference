namespace Winference.App.Services;

/// <summary>The window's lifetime. A failed close resets it so the app stays usable.</summary>
internal sealed class AppLifetime
{
    private CancellationTokenSource _source = new();

    public CancellationToken Token => _source.Token;
    public bool IsClosing { get; private set; }

    public void BeginClosing()
    {
        IsClosing = true;
        // One failing cancellation callback must not stop the rest of shutdown.
        try { _source.Cancel(); }
        catch (AggregateException error)
        {
            Diagnostics.AppDiagnostics.WriteException("lifetime.cancel.failed", error);
        }
    }

    public void Reset()
    {
        IsClosing = false;
        _source.Dispose();
        _source = new();
    }
}
