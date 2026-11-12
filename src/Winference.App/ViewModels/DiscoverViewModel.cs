using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Winference.App.Services;
using Winference.Core.Discover;
using Winference.Core.Models;
using Winference.Infrastructure.Discover;

namespace Winference.App.ViewModels;

/// <summary>
/// The Discover page: every model the app can get or already has, grouped by model family across
/// engines. It is a view over the shared model library, so Get, Use and progress behave the same
/// as everywhere else.
/// </summary>
public sealed class DiscoverViewModel : INotifyPropertyChanged
{
    public static readonly TimeSpan AutoRefreshAge = TimeSpan.FromDays(1);
    private readonly ModelLibraryViewModel _library;
    private readonly CatalogRefreshService _catalogs;
    private readonly AppLifetime _lifetime;
    private readonly Dictionary<string, DiscoverFamilyViewModel> _families = new(StringComparer.Ordinal);
    private string _query = string.Empty;
    private ModelFilter _runsOn = ModelFilter.All;
    private ModelCapabilities _needs = ModelCapabilities.None;
    private bool _hideWontFit = true;
    private bool _refreshing;
    private string? _problem;
    private DiscoverFamilyViewModel? _selected;
    private CommunityRepositoryViewModel? _selectedRepository;
    private bool _searchingCommunity;
    private string _communityMessage = string.Empty;
    private CancellationTokenSource? _communitySearch;

    internal DiscoverViewModel(ModelLibraryViewModel library, CatalogRefreshService catalogs, AppLifetime lifetime)
    {
        _library = library;
        _catalogs = catalogs;
        _lifetime = lifetime;
        _library.EntriesChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DiscoverSection> Sections { get; } = [];
    public ModelLibraryViewModel Library => _library;

    public DiscoverFamilyViewModel? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            if (value is not null) SelectedRepository = null;
            Changed(); Changed(nameof(DetailVisibility));
        }
    }

    public Visibility DetailVisibility => _selected is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Community GGUF repositories from the last Hugging Face search.</summary>
    public ObservableCollection<CommunityRepositoryViewModel> CommunityResults { get; } = [];
    public bool IsSearchingCommunity { get => _searchingCommunity; private set { _searchingCommunity = value; Changed(); } }
    public string CommunityMessage { get => _communityMessage; private set { _communityMessage = value; Changed(); } }
    public Visibility CommunityVisibility => _query.Trim().Length >= 2 ? Visibility.Visible : Visibility.Collapsed;
    public string CommunitySearchLabel => $"Search Hugging Face for \u201c{_query.Trim()}\u201d";

    public CommunityRepositoryViewModel? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (_selectedRepository == value) return;
            _selectedRepository = value;
            if (value is not null) Selected = null;
            Changed(); Changed(nameof(RepositoryDetailVisibility));
            if (value is { IsLoaded: false }) _ = LoadFilesAsync(value);
        }
    }

    public Visibility RepositoryDetailVisibility => _selectedRepository is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Searches only when asked, because the words are sent to Hugging Face.</summary>
    public async Task SearchCommunityAsync()
    {
        var words = _query.Trim();
        if (words.Length < 2) return;
        _communitySearch?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _communitySearch = cancellation;
        IsSearchingCommunity = true;
        CommunityMessage = string.Empty;
        try
        {
            var found = await _library.SearchCommunityAsync(words, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            CommunityResults.Clear();
            foreach (var repository in found) CommunityResults.Add(new(repository));
            CommunityMessage = found.Count == 0 ? "No GGUF models on Hugging Face match these words."
                : "Community uploads aren't tested by Winference. Each file is checked against Hugging Face's fingerprint when downloaded.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is HttpRequestException or Winference.Core.Runtime.RuntimeException)
        {
            CommunityMessage = error is Winference.Core.Runtime.RuntimeException runtime ? runtime.Message
                : "Hugging Face couldn't be reached. Check your internet connection and try again.";
        }
        finally
        {
            if (ReferenceEquals(_communitySearch, cancellation)) { _communitySearch = null; IsSearchingCommunity = false; }
        }
    }

    private async Task LoadFilesAsync(CommunityRepositoryViewModel repository)
    {
        repository.SetLoading();
        try
        {
            var files = await _library.ListCommunityFilesAsync(repository.Id, _lifetime.Token);
            repository.SetFiles(files.Select(file => new CommunityFileViewModel(file, _library.MemoryBytes)).ToArray());
        }
        catch (OperationCanceledException) when (_lifetime.IsClosing) { }
        catch (Exception error) when (error is HttpRequestException or Winference.Core.Runtime.RuntimeException)
        {
            repository.SetProblem(error is Winference.Core.Runtime.RuntimeException runtime ? runtime.Message
                : "Hugging Face couldn't be reached. Check your internet connection and try again.");
        }
    }

    /// <summary>Gets one community file through the shared library, so progress and Cancel behave as usual.</summary>
    public async Task GetCommunityFileAsync(CommunityFileViewModel file)
    {
        file.Entry ??= _library.AddCommunity(file.File);
        if (file.Entry.CanAct) await _library.PerformAsync(file.Entry);
    }

    public string Query
    {
        get => _query;
        set
        {
            value ??= string.Empty;
            if (_query == value) return;
            _query = value;
            Changed(); Changed(nameof(CommunityVisibility)); Changed(nameof(CommunitySearchLabel));
            ApplyView();
        }
    }

    public ModelFilter RunsOn
    {
        get => _runsOn;
        set { if (_runsOn == value) return; _runsOn = value; Changed(); ApplyView(); }
    }

    public bool HideWontFit
    {
        get => _hideWontFit;
        set { if (_hideWontFit == value) return; _hideWontFit = value; Changed(); ApplyView(); }
    }

    public void SetNeed(ModelCapabilities capability, bool required)
    {
        var updated = required ? _needs | capability : _needs & ~capability;
        if (updated == _needs) return;
        _needs = updated;
        ApplyView();
    }

    public bool IsRefreshing { get => _refreshing; private set { _refreshing = value; Changed(); } }
    public string? Problem { get => _problem; private set { _problem = value; Changed(); Changed(nameof(HasProblem)); } }
    public bool HasProblem => !string.IsNullOrEmpty(_problem);
    public bool IsEmpty => _library.IsChecked && Sections.Count == 0;
    public string EmptyMessage => _query.Trim().Length > 0 ? "No models match your search." : "No models match these filters.";

    public string UpdatedLabel => _catalogs.RefreshedUtc is { } refreshed
        ? "Model lists updated " + Ago(DateTimeOffset.UtcNow - refreshed)
        : "Using the model lists included with Winference";

    public string HardwareSummary => _library.HardwareSummary;

    /// <summary>Called when the page opens: refreshes the lists if they're more than a day old.</summary>
    public Task OpenAsync() => _catalogs.IsStale(AutoRefreshAge) ? RefreshAsync() : _library.RefreshAsync();

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var result = await _catalogs.RefreshAsync(_lifetime.Token);
            Problem = result.Problem;
            await _library.RefreshAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsClosing) { }
        finally
        {
            if (!_lifetime.IsClosing)
            {
                IsRefreshing = false;
                Changed(nameof(UpdatedLabel));
            }
        }
    }

    private void Rebuild()
    {
        var memory = _library.MemoryBytes;
        var grouped = _library.Entries.GroupBy(entry => ModelFamilyKey.For(entry.Entry.Family.DisplayName), StringComparer.Ordinal).ToArray();
        foreach (var group in grouped)
        {
            if (!_families.TryGetValue(group.Key, out var family)) _families[group.Key] = family = new(group.Key);
            family.Update(group.ToArray(), memory);
        }
        foreach (var stale in _families.Keys.Except(grouped.Select(group => group.Key)).ToArray()) _families.Remove(stale);
        if (_selected is not null && !_families.ContainsValue(_selected)) Selected = null;
        Changed(nameof(HardwareSummary));
        ApplyView();
    }

    private void ApplyView()
    {
        var visible = _families.Values.Where(Matches).ToArray();
        var filtered = _query.Trim().Length > 0 || _runsOn != ModelFilter.All || _needs != ModelCapabilities.None;
        var sections = filtered
            ? [new DiscoverSection("Results", visible.OrderByDescending(f => f.IsOnThisPc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))]
            : BuildSections(visible, _library.MemoryBytes);
        Sections.Clear();
        foreach (var section in sections.Where(section => section.Families.Count > 0)) Sections.Add(section);
        Changed(nameof(IsEmpty));
        Changed(nameof(EmptyMessage));
    }

    private static IEnumerable<DiscoverSection> BuildSections(IReadOnlyList<DiscoverFamilyViewModel> families, long memoryBytes)
    {
        var fitting = families.Where(family => family.BestFit is FitLevel.FitsWell).ToArray();
        yield return new("On this PC", families.Where(family => family.IsOnThisPc).OrderBy(family => family.Name, StringComparer.OrdinalIgnoreCase));
        // Larger models answer better but run slower. The sweet spot is the largest model that
        // stays quick: a third of the memory left after Windows, and no more than 8 GB.
        var quick = Math.Min(8_000_000_000L, (long)((memoryBytes - ModelFit.SystemReserveBytes) / 3.0));
        yield return new("Best for this PC", fitting.Where(family => !family.IsOnThisPc && family.LargestFittingBytes <= quick)
            .OrderByDescending(family => family.HasNpu).ThenByDescending(family => family.LargestFittingBytes).Take(8));
        yield return new("Most efficient: runs on the NPU", fitting.Where(family => family.HasNpu && !family.IsOnThisPc)
            .OrderBy(family => family.SmallestBytes).Take(12));
        yield return new("Thinks before answering", families.Where(family => family.Capabilities.HasFlag(ModelCapabilities.Reasoning))
            .OrderBy(family => family.SmallestBytes).Take(12));
        yield return new("Sees pictures", families.Where(family => family.Capabilities.HasFlag(ModelCapabilities.Vision))
            .OrderBy(family => family.SmallestBytes).Take(12));
        yield return new("All models", families.OrderBy(family => family.Name, StringComparer.OrdinalIgnoreCase));
    }

    private bool Matches(DiscoverFamilyViewModel family)
    {
        if (_hideWontFit && family.BestFit == FitLevel.WontFit && !family.IsOnThisPc) return false;
        if (_runsOn != ModelFilter.All && !family.Variants.Any(variant => ModelCatalogView.Matches(variant.Mode, _runsOn))) return false;
        if ((family.Capabilities & _needs) != _needs) return false;
        var text = _query.Trim();
        // "qwen3 4b" should find "Qwen 3 4B", so spacing and punctuation are ignored too.
        return text.Length == 0 || family.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            family.SearchKey.Contains(ModelFamilyKey.For(text), StringComparison.Ordinal);
    }

    private static string Ago(TimeSpan age) => age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} days ago";

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class DiscoverSection(string title, IEnumerable<DiscoverFamilyViewModel> families)
{
    public string Title { get; } = title;
    public IReadOnlyList<DiscoverFamilyViewModel> Families { get; } = families.ToArray();
}

/// <summary>One model across engines: its NPU, GPU + NPU and GPU builds side by side.</summary>
public sealed class DiscoverFamilyViewModel(string key) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; } = key;
    public ObservableCollection<DiscoverVariantViewModel> Variants { get; } = [];
    public string Name { get; private set; } = string.Empty;
    public string Publisher { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public ModelCapabilities Capabilities { get; private set; }
    public FitLevel BestFit { get; private set; }
    public bool IsOnThisPc { get; private set; }
    public bool HasNpu { get; private set; }
    public long SmallestBytes { get; private set; }
    public long LargestFittingBytes { get; private set; }
    public string SearchText { get; private set; } = string.Empty;
    public string SearchKey { get; private set; } = string.Empty;

    public string EnginesLabel => string.Join(" · ", Variants.Select(variant => variant.Badge).Distinct());
    public string SizeLabel => SmallestBytes > 0 ? $"From {StorageCheck.Format(SmallestBytes)}" : string.Empty;
    public string FitLabel => IsOnThisPc ? "On this PC" : ModelFit.Label(BestFit);
    public string CapabilityLabel => string.Join(" · ", new[]
    {
        Capabilities.HasFlag(ModelCapabilities.Reasoning) ? "Thinks" : null,
        Capabilities.HasFlag(ModelCapabilities.Vision) ? "Sees pictures" : null,
        Capabilities.HasFlag(ModelCapabilities.Tools) ? "Uses tools" : null,
        Capabilities.HasFlag(ModelCapabilities.Coding) ? "Coding" : null,
    }.Where(label => label is not null));
    public Visibility CapabilityVisibility => Capabilities == ModelCapabilities.None ? Visibility.Collapsed : Visibility.Visible;
    public string AutomationName => $"{Name}, runs on {EnginesLabel}, {FitLabel}";

    internal void Update(IReadOnlyList<ModelEntryViewModel> entries, long memoryBytes)
    {
        var ordered = entries.OrderBy(entry => entry.Entry.Profile.ExecutionMode switch
            { ExecutionMode.NpuOnly => 0, ExecutionMode.GpuNpu => 1, _ => 2 })
            .ThenBy(entry => entry.Entry.Profile.DownloadBytes ?? long.MaxValue).ToArray();
        var keep = Variants.Where(variant => ordered.Contains(variant.Entry)).ToDictionary(variant => variant.Entry);
        Variants.Clear();
        foreach (var entry in ordered)
        {
            var variant = keep.GetValueOrDefault(entry) ?? new DiscoverVariantViewModel(entry);
            variant.SetMemory(memoryBytes);
            Variants.Add(variant);
        }
        // The shortest name is usually the cleanest, e.g. "Llama 3.2 1B" over "Llama 3.2 1B Instruct GGUF".
        Name = ordered.Select(entry => entry.Name).OrderBy(name => name.Length).First();
        Publisher = ModelMakers.For(Name)
            ?? ordered.Select(entry => entry.Publisher).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && p != "FastFlowLM catalog")
            ?? ordered[0].Publisher;
        Description = ordered.Select(entry => entry.Description).OrderByDescending(text => text.Length).First();
        Capabilities = ordered.Aggregate(ModelCapabilities.None, (all, entry) => all | entry.Entry.Profile.Capabilities);
        IsOnThisPc = ordered.Any(entry => entry.Entry.IsOnThisPc);
        HasNpu = ordered.Any(entry => entry.Entry.Profile.ExecutionMode == ExecutionMode.NpuOnly);
        var sizes = Variants.Where(variant => variant.SizeBytes > 0).ToArray();
        SmallestBytes = sizes.Length > 0 ? sizes.Min(variant => variant.SizeBytes) : 0;
        LargestFittingBytes = sizes.Where(variant => variant.Fit == FitLevel.FitsWell).Select(variant => variant.SizeBytes).DefaultIfEmpty().Max();
        var known = Variants.Select(variant => variant.Fit).Where(fit => fit != FitLevel.Unknown).ToArray();
        BestFit = known.Length > 0 ? known.Min() : FitLevel.Unknown;
        SearchText = string.Join(' ', ordered.Select(entry => $"{entry.Name} {entry.Publisher} {entry.Badge} {entry.Entry.Profile.Artifact.Source}"));
        SearchKey = string.Join('|', ordered.Select(entry => ModelFamilyKey.For(entry.Entry.Family.DisplayName)).Append(Key));
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
}

/// <summary>One engine's build of a model. Actions and progress come straight from the shared library entry.</summary>
public sealed class DiscoverVariantViewModel : INotifyPropertyChanged
{
    private long _memoryBytes;

    internal DiscoverVariantViewModel(ModelEntryViewModel entry) => Entry = entry;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModelEntryViewModel Entry { get; }
    internal ExecutionMode Mode => Entry.Entry.Profile.ExecutionMode;
    public string Badge => Entry.Badge;
    public long SizeBytes => Entry.Entry.Profile.DownloadBytes is > 0 and var download ? download : Entry.Entry.Profile.StorageBytes ?? 0;
    public FitLevel Fit => ModelFit.Estimate(SizeBytes, _memoryBytes);
    public string TierLabel => Entry.Entry.Profile.Tier == CatalogTier.Tested ? "Tested" : "Community";

    public string Note => Mode switch
    {
        ExecutionMode.NpuOnly => "Most efficient. Best on battery.",
        ExecutionMode.GpuNpu => "Shares the work between graphics and NPU.",
        _ => "Uses your graphics. Fastest when plugged in."
    };

    public string Summary => string.Join(" · ", new[]
    {
        TierLabel,
        Entry.Entry.Profile.Artifact.Quantization,
        SizeBytes > 0 ? StorageCheck.Format(SizeBytes) : null,
        Entry.Entry.IsOnThisPc ? null : ModelFit.Label(Fit)
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public string TechnicalDetails => string.Join(Environment.NewLine, new[]
    {
        $"Engine: {Entry.Entry.Profile.Runtime}",
        $"Format: {Entry.Entry.Profile.Artifact.FormatLabel}",
        $"Source: {Entry.Entry.Profile.Artifact.Source}",
        Entry.Entry.Profile.Artifact.Revision is { Length: > 0 } revision ? $"Version: {revision}" : null,
        Entry.Entry.Profile.Artifact.Sha256 is { } sha ? $"SHA-256: {sha}" : null,
        $"Model ID: {Entry.RuntimeModelId ?? Entry.Entry.Profile.ProfileId}",
        Entry.Entry.Profile.InstalledPath is { } path ? $"Location: {path}" : null,
        Entry.Entry.Profile.Evidence
    }.Where(line => !string.IsNullOrWhiteSpace(line)));

    internal void SetMemory(long memoryBytes)
    {
        _memoryBytes = memoryBytes;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
}

/// <summary>One Hugging Face repository from search. Its files are listed when it's opened.</summary>
public sealed class CommunityRepositoryViewModel(HuggingFaceRepository repository) : INotifyPropertyChanged
{
    private string _status = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => repository.Id;
    public string Name => repository.Name.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase) ? repository.Name[..^5] : repository.Name;
    public string Publisher => ModelMakers.For(Name) is { } maker ? $"{maker} · uploaded by {repository.Owner}" : $"Uploaded by {repository.Owner}";
    public string Popularity => $"{Count(repository.Downloads)} downloads · {Count(repository.Likes)} likes";
    public string SourcePage => $"https://huggingface.co/{repository.Id}";
    public bool IsLoaded { get; private set; }
    public ObservableCollection<CommunityFileViewModel> Files { get; } = [];
    public string Status { get => _status; private set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public string AutomationName => $"{Name}, community upload by {repository.Owner}, {Popularity}";

    internal void SetLoading() { IsLoaded = true; Status = "Listing files…"; }

    internal void SetFiles(IReadOnlyList<CommunityFileViewModel> files)
    {
        Files.Clear();
        foreach (var file in files) Files.Add(file);
        Status = files.Count == 0 ? "This repository has no single-file GGUF models Winference can use." : string.Empty;
    }

    internal void SetProblem(string message) { IsLoaded = false; Status = message; }

    private static string Count(long value) => value >= 1_000_000 ? $"{value / 1_000_000d:0.#}M" : value >= 1_000 ? $"{value / 1_000d:0.#}K" : value.ToString();
}

/// <summary>One downloadable file (a quantization) in a community repository.</summary>
public sealed class CommunityFileViewModel(HuggingFaceFile file, long memoryBytes) : INotifyPropertyChanged
{
    private ModelEntryViewModel? _entry;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HuggingFaceFile File { get; } = file;
    public string FileName => File.FileName;
    /// <summary>Q4_K_M is the usual balance of size and answer quality.</summary>
    public bool IsRecommended => File.FileName.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase);
    public string Summary => string.Join(" · ", new[] { IsRecommended ? "Recommended" : string.Empty, StorageCheck.Format(File.LengthBytes),
            ModelFit.Label(ModelFit.Estimate(File.LengthBytes, memoryBytes)) }
        .Where(part => part.Length > 0));

    /// <summary>The library row created when Get is first chosen; its status and progress drive the UI.</summary>
    public ModelEntryViewModel? Entry
    {
        get => _entry;
        set { _entry = value; PropertyChanged?.Invoke(this, new(string.Empty)); }
    }

    public string ActionLabel => _entry?.ActionLabel is { Length: > 0 } label ? label : "Get";
}
