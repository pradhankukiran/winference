namespace Winference.Core.Hybrid;

/// <summary>Verified native Windows Ryzen AI payload, separate from model files.</summary>
public sealed record HybridRuntimeInstallation(
    string RuntimeDirectory, string ExecutablePath, string ReleaseTag,
    string ArchiveSha256, string ManifestPath);

public sealed record HybridRuntimeStatus(
    bool IsInstalled, string Status, long DownloadBytes,
    HybridRuntimeInstallation? Installation);
