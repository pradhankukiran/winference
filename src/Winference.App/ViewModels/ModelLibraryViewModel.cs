using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Winference.App.Diagnostics;
using Winference.App.Services;
using Winference.Core.Downloads;
using Winference.Core.Gpu;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Gpu;
using Winference.Infrastructure.Hybrid;
using Winference.Infrastructure.Models;
using Winference.Infrastructure.Selection;

namespace Winference.App.ViewModels;

/// <summary>
/// The one model list behind the Models page, the chat picker and first run. Every
/// runtime goes through the same Get, Use, Add and Remove paths here. One download
/// runs at a time, independently of whichever model is loaded.
/// </summary>
public sealed class ModelLibraryViewModel : INotifyPropertyChanged
{
    private readonly ShellViewModel _shell;
    private readonly ISetupService _setup;
    private readonly ISetupService _downloader;
    private readonly ModelLibraryService _library;
    private readonly GpuModelService _gpuModels;
    private readonly GpuRuntimeAcquisition _gpuRuntime;
    private readonly HybridModelService _hybridModels;
    private readonly HybridRuntimeAcquisition _hybridRuntime;
    private readonly AppPreferencesStore _preferences;
    private readonly string _dataRoot;
    private readonly DialogService _dialogs;
    private readonly AppLifetime _lifetime;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<Task<string?>> _pickModelFile;
    private readonly Dictionary<string, ModelEntryViewModel> _entries = new(StringComparer.Ordinal);
    private ModelSessionService _session = null!;
    private SetupInspection? _inspection;
    private GpuRuntimeStatus? _gpuStatus;
    private Task? _refresh;
    private Task? _download;
    private CancellationTokenSource? _downloadCancellation;
    private ModelEntryViewModel? _downloading;
    private ModelEntryViewModel? _recommended;
    private ModelEntryViewModel? _offered;
    private ModelFilter _filter;
    private string _query = string.Empty;
    private string? _error;
    private string? _notice;
    private bool _checked;

    internal ModelLibraryViewModel(ShellViewModel shell, ISetupService setup, ISetupService downloader, ModelLibraryService library,
        GpuModelService gpuModels, GpuRuntimeAcquisition gpuRuntime, HybridModelService hybridModels, HybridRuntimeAcquisition hybridRuntime,
        AppPreferencesStore preferences, string dataRoot, DialogService dialogs, AppLifetime lifetime, DispatcherQueue dispatcher,
        Func<Task<string?>> pickModelFile)
    {
        _shell = shell;
        _setup = setup;
        _downloader = downloader;
        _library = library;
        _gpuModels = gpuModels;
        _gpuRuntime = gpuRuntime;
        _hybridModels = hybridModels;
        _hybridRuntime = hybridRuntime;
        _preferences = preferences;
        _dataRoot = dataRoot;
        _dialogs = dialogs;
        _lifetime = lifetime;
        _dispatcher = dispatcher;
        _pickModelFile = pickModelFile;
        _filter = preferences.Read().ModelFilter;
        _shell.PropertyChanged += Shell_PropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ModelEntryViewModel> OnThisPc { get; } = [];
    public ObservableCollection<ModelEntryViewModel> Available { get; } = [];
    public bool HasOnThisPc => OnThisPc.Count > 0;
    public bool HasAvailable => Available.Count > 0;
    public bool IsEmpty => _checked && OnThisPc.Count == 0 && Available.Count == 0;
    public bool IsChecking => !_checked;
    public string EmptyMessage => _query.Trim().Length > 0 ? "No models match your search."
        : _filter == ModelFilter.All ? "No models can run on this PC yet."
        : $"No {FilterLabel(_filter)} models can run on this PC.";

    public string? Error { get => _error; private set { _error = value; Changed(); Changed(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public string? Notice { get => _notice; private set { _notice = value; Changed(); Changed(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public ModelFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value) return;
            _filter = value;
            _preferences.Write(_preferences.Read() with { ModelFilter = value });
            Changed();
            ApplyView();
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value) return;
            _query = value ?? string.Empty;
            Changed();
            ApplyView();
        }
    }

    public string RecommendedName => _recommended?.Name ?? string.Empty;
    public string RecommendedDetail => _recommended is { } entry ? $"Runs on {entry.Badge}" : string.Empty;
    public string RecommendedAction => _recommended is { } entry ? (entry.Entry.IsOnThisPc ? $"Use {entry.Name}" : $"Get {entry.Name} ({entry.SizeLabel})") : string.Empty;
    public bool HasRecommendation => _recommended is not null;
    public bool IsUnsupported => _checked && _recommended is null;
    public string HardwareProblem => "Winference needs a supported AMD Ryzen AI NPU or Radeon graphics to run its models. If you're missing a driver, install it and check again.";
    public string DownloadStatus => _downloading?.StatusLine ?? string.Empty;
    public bool IsDownloading => _downloading is not null;
    public bool IsDownloadIndeterminate => _downloading?.IsProgressIndeterminate ?? true;
    public double DownloadValue => _downloading?.ProgressValue ?? 0;

    internal void Attach(ModelSessionService session) => _session = session;
    internal Task? PendingDownload => _download;

    public Task RefreshAsync()
    {
        if (_refresh is { IsCompleted: false } running) return running;
        _refresh = RefreshCoreAsync();
        return _refresh;
    }

    private async Task RefreshCoreAsync()
    {
        var token = _lifetime.Token;
        try
        {
            var inspectionTask = _setup.InspectAsync(token);
            var gpuTask = _gpuModels.ReadRegisteredAsync(token);
            var gpuRuntimeTask = _gpuRuntime.InspectAsync(token);
            var hybridTask = _hybridModels.ReadRegisteredAsync(token);
            await Task.WhenAll(inspectionTask, gpuTask, gpuRuntimeTask, hybridTask);
            var inspection = await inspectionTask;
            var snapshot = await _library.InspectAsync(inspection, token);
            if (_lifetime.IsClosing) return;
            _inspection = inspection;
            _gpuStatus = await gpuRuntimeTask;
            Notice = snapshot.Notice;
            var gpuSupported = inspection.Graphics.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
            Rebuild(ModelCatalogView.Build(snapshot, gpuSupported), await gpuTask, await hybridTask, inspection.Models);
            _checked = true;
            Error = null;
            ApplyView();
        }
        catch (OperationCanceledException) when (_lifetime.IsClosing) { }
        catch (Exception error)
        {
            if (_lifetime.IsClosing) return;
            _checked = true;
            Error = error is RuntimeException runtime ? runtime.Message : "Installed models could not be checked. Try Refresh.";
            ApplyView();
        }
    }
}
