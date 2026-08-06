namespace Winference.Core.Models;

/// <summary>One mode-specific entry shown in the unified model library.</summary>
public sealed record LibraryProfile(
    string ProfileId,
    ExecutionMode ExecutionMode,
    string Runtime,
    ModelArtifact Artifact,
    ModelCompatibility Compatibility,
    long? DownloadBytes,
    long? StorageBytes,
    string? InstalledPath,
    bool CanActivate,
    bool CanDownload,
    bool CanImport,
    string Evidence)
{
    public string ModeLabel => ExecutionMode switch
    {
        ExecutionMode.GpuOnly => "GPU",
        ExecutionMode.GpuNpu => "GPU + NPU",
        ExecutionMode.NpuOnly => "NPU",
        _ => "Unknown mode"
    };

    public string SizeLabel => StorageBytes is { } stored
        ? $"{stored / 1_000_000_000d:0.00} GB on disk"
        : DownloadBytes is { } download ? $"{download / 1_000_000_000d:0.00} GB download" : "Size unavailable";

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentNullException.ThrowIfNull(Artifact);
        ArgumentNullException.ThrowIfNull(Compatibility);
        Artifact.Validate();
        if (!Enum.IsDefined(ExecutionMode)) throw new ArgumentOutOfRangeException(nameof(ExecutionMode));
        if (string.IsNullOrWhiteSpace(Runtime) || Runtime.Length > 120)
            throw new ArgumentException("A library profile must name its runtime.", nameof(Runtime));
        if (DownloadBytes is < 0 || StorageBytes is < 0) throw new ArgumentOutOfRangeException(nameof(DownloadBytes));
        if (Evidence.Length > 2_000) throw new ArgumentException("The evidence label is too long.", nameof(Evidence));
    }
}

/// <summary>One family and all of its mode-specific profiles.</summary>
public sealed record LibraryFamily(ModelFamily Family, IReadOnlyList<LibraryProfile> Profiles)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Family);
        Family.Validate();
        if (Profiles is null || Profiles.Count == 0) throw new ArgumentException("A library family needs at least one profile.", nameof(Profiles));
        foreach (var profile in Profiles) profile.Validate();
        if (Profiles.Select(profile => profile.ProfileId).Distinct(StringComparer.Ordinal).Count() != Profiles.Count)
            throw new ArgumentException("A library family cannot repeat a profile ID.", nameof(Profiles));
    }
}

/// <summary>A read-only snapshot assembled from the approved catalogs and registered local files.</summary>
public sealed record ModelLibrarySnapshot(
    IReadOnlyList<LibraryFamily> Families,
    string HardwareSummary,
    string? Notice = null)
{
    public int ProfileCount => Families.Sum(family => family.Profiles.Count);
    public int InstalledCount => Families.Sum(family => family.Profiles.Count(profile => profile.Compatibility.IsInstalled));
}
