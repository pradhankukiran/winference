namespace Winference.Core.Downloads;

/// <summary>The lifecycle of one explicit model download.</summary>
public enum DownloadState
{
    Queued,
    Preparing,
    Downloading,
    Verifying,
    Completed,
    Cancelling,
    Cancelled,
    Failed
}

/// <summary>What the selected route can actually do. Do not advertise resume when the route cannot use partial bytes.</summary>
public sealed record DownloadCapabilities(bool AppOwned, bool CanCancel, bool CanResume);

/// <summary>Honest progress. A null total means indeterminate, never an invented percentage.</summary>
public sealed record DownloadProgress(
    DownloadState State,
    string Message,
    long? CompletedBytes = null,
    long? TotalBytes = null)
{
    public double? Percent => TotalBytes is > 0 && CompletedBytes is { } completed
        ? Math.Clamp(100d * completed / TotalBytes.Value, 0, 100)
        : null;

    public bool IsTerminal => State is DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed;
}

/// <summary>The final identity of a completed download.</summary>
public sealed record DownloadResult(
    string ArtifactId,
    DownloadState State,
    string? InstalledPath,
    string Message,
    string? ErrorCode = null)
{
    public bool Succeeded => State == DownloadState.Completed;
}
