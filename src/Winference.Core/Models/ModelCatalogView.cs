namespace Winference.Core.Models;

/// <summary>The processor filter on the model list. It narrows what is shown and never changes the loaded model.</summary>
public enum ModelFilter { All, Npu, GpuNpu, Gpu }

/// <summary>One runnable model as a person sees it: a name, where it runs, and whether it is here yet.</summary>
public sealed record ModelCatalogEntry(ModelFamily Family, LibraryProfile Profile)
{
    public bool IsOnThisPc => Profile.Compatibility.IsInstalled ||
        Profile.Compatibility.State == ModelAvailability.Unavailable && Profile.InstalledPath is not null;
    public bool NeedsRepair => !Profile.Compatibility.IsInstalled && IsOnThisPc;
    public bool CanGet => Profile.CanDownload;
}

/// <summary>Decides what the model list shows. Pure, so the rules can be tested without a window.</summary>
public static class ModelCatalogView
{
    /// <param name="gpuSupported">False hides GPU downloads this PC could not run. Models already here stay visible.</param>
    public static IReadOnlyList<ModelCatalogEntry> Build(ModelLibrarySnapshot snapshot, bool gpuSupported)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Families
            .SelectMany(family => family.Profiles.Select(profile => new ModelCatalogEntry(family.Family, profile)))
            .Where(entry => IsListed(entry, gpuSupported))
            .ToArray();
    }

    public static IReadOnlyList<ModelCatalogEntry> Apply(IEnumerable<ModelCatalogEntry> entries, ModelFilter filter, string? query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = query?.Trim() ?? string.Empty;
        return entries
            .Where(entry => Matches(entry.Profile.ExecutionMode, filter))
            .Where(entry => text.Length == 0 || MatchesText(entry, text))
            .OrderByDescending(entry => entry.IsOnThisPc)
            // Downloads list smallest first, so a huge model is never the first thing offered.
            .ThenBy(entry => entry.IsOnThisPc ? 0 : entry.Profile.DownloadBytes ?? long.MaxValue)
            .ThenBy(entry => entry.Family.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Profile.ExecutionMode)
            .ToArray();
    }

    public static bool Matches(ExecutionMode mode, ModelFilter filter) => filter switch
    {
        ModelFilter.Npu => mode == ExecutionMode.NpuOnly,
        ModelFilter.GpuNpu => mode == ExecutionMode.GpuNpu,
        ModelFilter.Gpu => mode == ExecutionMode.GpuOnly,
        _ => true
    };

    private static bool IsListed(ModelCatalogEntry entry, bool gpuSupported)
    {
        // The hybrid placeholder is a way to add a folder, not a model. Add a model covers it.
        if (entry.Profile.CanImport) return false;
        if (entry.IsOnThisPc) return true;
        if (entry.Profile.Compatibility.State == ModelAvailability.Unavailable) return false;
        return entry.Profile.ExecutionMode != ExecutionMode.GpuOnly || gpuSupported;
    }

    private static bool MatchesText(ModelCatalogEntry entry, string text) =>
        new[] { entry.Family.DisplayName, entry.Family.Publisher, entry.Profile.ModeLabel }
            .Any(value => value.Contains(text, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>Chooses the one model first run offers, so a new user does not face the whole catalog.</summary>
public static class ModelRecommender
{
    private const long RoomyMemoryBytes = 15L * 1024 * 1024 * 1024;
    private const string LargeDefault = "flm:phi4-mini-it:4b";
    private static readonly string[] SmallDefaults = ["flm:llama3.2:1b", "flm:gemma3:1b", "flm:qwen3:0.6b"];

    public static ModelCatalogEntry? Pick(IReadOnlyList<ModelCatalogEntry> entries, long memoryBytes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ready = entries.FirstOrDefault(entry => entry.Profile.Compatibility.IsInstalled);
        if (ready is not null) return ready;
        var candidates = entries.Where(entry => entry.CanGet).ToArray();
        var preferred = memoryBytes >= RoomyMemoryBytes ? SmallDefaults.Prepend(LargeDefault) : SmallDefaults;
        foreach (var id in preferred)
            if (candidates.FirstOrDefault(entry => entry.Profile.Artifact.Id == id) is { } match) return match;
        return candidates
            .OrderBy(entry => entry.Profile.ExecutionMode == ExecutionMode.NpuOnly ? 0 : 1)
            .ThenBy(entry => entry.Profile.DownloadBytes ?? long.MaxValue)
            .FirstOrDefault();
    }
}

/// <summary>Whether a download fits, checked before it starts rather than failing partway through.</summary>
public static class StorageCheck
{
    public const long HeadroomBytes = 256L * 1024 * 1024;
}
