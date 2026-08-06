namespace Winference.Core.Models;

/// <summary>A read-only snapshot of one explicitly reviewed, app-owned file.</summary>
public sealed record ModelRemovalFile(string Name, long LengthBytes, DateTime LastWriteUtc, string Sha256);

/// <summary>Issued by the removal service; not a caller-supplied deletion path.</summary>
public sealed record ModelRemovalPlan(
    Guid Id, string ProfileId, string ArtifactId, ExecutionMode ExecutionMode,
    string DisplayName, string ModelId, string ModelDirectory,
    IReadOnlyList<ModelRemovalFile> Files, long TotalBytes,
    LibraryProfile Profile, Guid? GpuRegistrationId = null);

/// <summary>Physical deletion totals. A pending private cleanup never deletes external files.</summary>
public sealed record ModelRemovalResult(long RemovedBytes, int RemovedFiles,
    bool CleanupPending = false, string? RecoveryDirectory = null);
