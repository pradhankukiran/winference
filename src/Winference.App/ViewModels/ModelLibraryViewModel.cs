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

    private void Rebuild(IReadOnlyList<ModelCatalogEntry> entries, IReadOnlyList<GpuModelRegistration> gpu,
        IReadOnlyList<HybridModelRegistration> hybrid, IReadOnlyList<SetupModel> npu)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var profile = entry.Profile;
            seen.Add(profile.ProfileId);
            var gpuModel = gpu.FirstOrDefault(model => model.Profile.ProfileId == profile.ProfileId);
            var hybridModel = hybrid.FirstOrDefault(model => model.Profile.ProfileId == profile.ProfileId);
            var npuModel = profile.Artifact.Id.StartsWith("flm:", StringComparison.Ordinal)
                ? npu.FirstOrDefault(model => profile.Artifact.Id == "flm:" + model.Id) : null;
            var download = _library.GpuCatalog.Models.FirstOrDefault(model => model.Sha256 == profile.Artifact.Sha256);
            var canRemove = _library.CanRemove(profile);
            if (_entries.TryGetValue(profile.ProfileId, out var existing)) existing.Update(entry, gpuModel, hybridModel, npuModel, download, canRemove);
            else _entries[profile.ProfileId] = new(entry, gpuModel, hybridModel, npuModel, download, canRemove);
        }
        foreach (var stale in _entries.Keys.Where(key => !seen.Contains(key)).ToArray())
            if (!ReferenceEquals(_entries[stale], _downloading)) _entries.Remove(stale);
        _recommended?.SetRecommended(false);
        _recommended = ModelRecommender.Pick(entries, _inspection?.MemoryBytes ?? 0) is { } picked
            ? _entries.GetValueOrDefault(picked.Profile.ProfileId) : null;
        _recommended?.SetRecommended(true);
    }

    private void ApplyView()
    {
        UpdateStatuses();
        var visible = ModelCatalogView.Apply(_entries.Values.Select(entry => entry.Entry), _filter, _query)
            .Select(entry => _entries[entry.Profile.ProfileId]).ToArray();
        Sync(OnThisPc, visible.Where(entry => entry.Entry.IsOnThisPc));
        Sync(Available, visible.Where(entry => !entry.Entry.IsOnThisPc).OrderByDescending(entry => entry.IsRecommended));
        foreach (var name in new[] { nameof(HasOnThisPc), nameof(HasAvailable), nameof(IsEmpty), nameof(IsChecking), nameof(EmptyMessage),
            nameof(RecommendedName), nameof(RecommendedDetail), nameof(RecommendedAction), nameof(HasRecommendation),
            nameof(IsUnsupported), nameof(HardwareProblem) }) Changed(name);
    }

    private static void Sync(ObservableCollection<ModelEntryViewModel> target, IEnumerable<ModelEntryViewModel> items)
    {
        var wanted = items.ToArray();
        if (target.SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var item in wanted) target.Add(item);
    }

    private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ShellViewModel.Chat) or nameof(ShellViewModel.FriendlyStatus) or nameof(ShellViewModel.HasModelActivity))
            UpdateStatuses();
    }

    private void UpdateStatuses()
    {
        var chat = _shell.Chat;
        var activeId = chat is { CanLoadModel: true } ? _shell.ActiveModelId : null;
        var busy = _shell.HasModelActivity;
        foreach (var entry in _entries.Values)
        {
            var selected = activeId is not null && entry.RuntimeModelId == activeId;
            var failure = selected && chat?.HasErrorNotice == true ? chat.ConnectionDetail : null;
            var status = ModelStatusRules.Resolve(entry.Entry.Profile.Compatibility.IsInstalled, entry.Entry.NeedsRepair,
                ReferenceEquals(entry, _downloading), failure is not null, selected,
                chat?.IsConnecting == true || busy, chat?.ActiveSession is not null);
            entry.SetStatus(status, CanAct(status), failure);
        }
    }

    private bool CanAct(ModelStatus status) => status == ModelStatus.Downloading ||
        !_shell.HasModelActivity && (status is ModelStatus.Ready or ModelStatus.Failed || _downloading is null);

    /// <summary>Carries out the row's main button.</summary>
    public async Task PerformAsync(ModelEntryViewModel entry)
    {
        switch (entry.Action)
        {
            case ModelAction.Get: await GetAsync(entry, confirm: true); break;
            case ModelAction.Use: await UseAsync(entry); break;
            case ModelAction.Cancel: CancelDownload(); break;
            case ModelAction.Repair: await AddAsync(); break;
        }
    }

    /// <summary>First run's one button. The size is already on it, so it skips the extra question.</summary>
    public async Task ActOnRecommendationAsync()
    {
        if (_recommended is not { } entry) return;
        if (entry.Entry.IsOnThisPc) await UseAsync(entry);
        else await GetAsync(entry, confirm: false);
    }

    private async Task UseAsync(ModelEntryViewModel entry)
    {
        Error = null;
        ClearOffer();
        if (entry.Gpu is { } gpu) await _session.UseGpuAsync(gpu);
        else if (entry.Hybrid is { } hybrid) await _session.UseHybridAsync(hybrid);
        else if (entry.Npu is { } npu)
        {
            // Files can be present while the runtime is not; Get fetches only what is missing.
            if (_inspection?.RuntimeInstalled == false) await GetAsync(entry, confirm: true);
            else await _session.UseNpuAsync(npu);
        }
    }

    private long ComponentBytes(ModelEntryViewModel entry) => entry.Npu is not null
        ? _inspection?.RuntimeInstalled == false ? _inspection.RuntimeDownloadBytes ?? 0 : 0
        : _gpuStatus is { IsInstalled: false } status ? status.DownloadBytes : 0;

    private async Task GetAsync(ModelEntryViewModel entry, bool confirm)
    {
        if (_downloading is not null) return;
        Error = null;
        var modelBytes = entry.Entry.Profile.DownloadBytes ?? 0;
        var componentBytes = ComponentBytes(entry);
        var needed = modelBytes + componentBytes;
        if (FreeBytes() is { } free && !StorageCheck.HasRoom(needed, free))
        {
            await _dialogs.InformAsync("Not enough storage",
            [
                $"{entry.Name} needs {StorageCheck.Format(needed + StorageCheck.HeadroomBytes)} free, and this drive has {StorageCheck.Format(free)}.",
                "Free up some space, then try again."
            ]);
            return;
        }
        if (confirm)
        {
            var lines = new List<string> { $"{StorageCheck.Format(needed)} download · Runs on {entry.Badge}" };
            if (componentBytes > 0) lines.Add($"Includes the {StorageCheck.Format(componentBytes)} component this model needs.");
            lines.Add("You can keep chatting while it downloads.");
            var terms = entry.Download is { } source ? ("Model source and terms", source.SourcePage) : ((string, Uri)?)null;
            if (!await _dialogs.ConfirmAsync($"Get {entry.Name}?", lines.ToArray(), "Get", primaryIsDefault: true, link: terms)) return;
        }
        _download = RunDownloadAsync(entry);
        await _download;
    }

    private async Task RunDownloadAsync(ModelEntryViewModel entry)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _downloadCancellation = cancellation;
        _downloading = entry;
        ReportDownload(entry, "Preparing…", null);
        UpdateStatuses();
        DownloadChanged();
        string? failure = null;
        var completed = false;
        try
        {
            if (entry.Npu is { } npu)
            {
                await _downloader.DownloadAsync(new(npu.Id, ExecutionMode.NpuOnly, _inspection!.DefaultModelStore, npu.SuggestedContextLength),
                    new Progress<SetupProgress>(value => ReportDownload(entry, DownloadLabel(value.Stage), value.Percent)), cancellation.Token);
            }
            else if (entry.Download is { } gpu)
            {
                await _library.DownloadGpuAsync(gpu, new Progress<DownloadProgress>(value =>
                    ReportDownload(entry, value.State == DownloadState.Verifying ? "Verifying…" : "Downloading…", value.Percent)), cancellation.Token);
                if (_gpuStatus is { IsInstalled: false })
                    await _gpuRuntime.AcquireAsync(new Progress<SetupProgress>(value =>
                        ReportDownload(entry, "Getting the GPU component…", value.Percent)), cancellation.Token);
            }
            completed = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppDiagnostics.WriteException("model.download.failed", error);
            failure = error is RuntimeException runtime ? runtime.Message
                : "The download did not finish. Downloaded parts were kept, so Get can pick up where it stopped.";
        }
        finally
        {
            _downloadCancellation = null;
            _downloading = null;
            _shell.ClearDownloadActivity();
        }
        if (_lifetime.IsClosing) return;
        DownloadChanged();
        await RefreshAsync();
        if (failure is not null)
        {
            Error = failure;
            _shell.ReportShellError(failure);
        }
        else if (completed && _entries.GetValueOrDefault(entry.Key) is { } finished) Finish(finished);
    }

    /// <summary>Starts the model if nothing is loaded. Otherwise it asks, without interrupting the chat.</summary>
    private void Finish(ModelEntryViewModel entry)
    {
        if (_shell.Chat is { CanLoadModel: true, IsShutDown: false }) Offer(entry);
        else _dispatcher.TryEnqueue(() => _ = UseAsync(entry));
    }

    private void Offer(ModelEntryViewModel entry)
    {
        _offered = entry;
        _shell.OfferModel(entry.Name);
    }

    public void ClearOffer()
    {
        _offered = null;
        _shell.ClearOffer();
    }

    public Task UseOfferedAsync() => _offered is { } entry ? UseAsync(entry) : Task.CompletedTask;

    public void CancelDownload()
    {
        if (_downloading is { } entry) ReportDownload(entry, "Cancelling…", null);
        _downloadCancellation?.Cancel();
    }

    private void ReportDownload(ModelEntryViewModel entry, string message, double? percent)
    {
        if (_lifetime.IsClosing || !ReferenceEquals(entry, _downloading)) return;
        entry.ReportDownload(message, percent);
        _shell.ReportDownloadActivity($"{entry.Name}: {entry.StatusLine}", percent);
        DownloadChanged();
    }

    private void DownloadChanged()
    {
        foreach (var name in new[] { nameof(DownloadStatus), nameof(IsDownloading), nameof(IsDownloadIndeterminate), nameof(DownloadValue) }) Changed(name);
    }

    private static string DownloadLabel(string stage) => stage switch
    {
        "DownloadingRuntime" or "InstallingRuntime" or "VerifyingRuntime" => "Getting the NPU component…",
        "DownloadingModel" => "Downloading…",
        "CheckingFiles" => "Verifying…",
        _ => "Preparing…"
    };

    private long? FreeBytes()
    {
        if (TestHardware.FreeBytesOverride() is { } simulated) return simulated;
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dataRoot))!).AvailableFreeSpace; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>One button for any model on disk. The file chosen decides which runtime it belongs to.</summary>
    public async Task AddAsync()
    {
        if (_session.IsChanging) return;
        await _session.RunAsync(AddCoreAsync);
    }

    private async Task AddCoreAsync()
    {
        try
        {
            var path = await _pickModelFile();
            if (path is null || _lifetime.IsClosing) return;
            Error = null;
            string? key;
            var progress = new Progress<SetupProgress>(value => { if (!_lifetime.IsClosing) _shell.ReportModelActivity(value.Message); });
            if (path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                _shell.ReportModelActivity("Reading and verifying the model file…");
                key = (await _gpuModels.RegisterAsync(path, progress, _lifetime.Token)).Profile.ProfileId;
            }
            else if (string.Equals(Path.GetFileName(path), "genai_config.json", StringComparison.OrdinalIgnoreCase))
            {
                _shell.ReportModelActivity("Reading and verifying the model folder…");
                key = (await _hybridModels.RegisterAsync(Path.GetDirectoryName(path)!, progress, _lifetime.Token)).Profile.ProfileId;
            }
            else
            {
                ReportFailure("Choose a .gguf model file, or the genai_config.json file inside a Ryzen AI model folder.");
                return;
            }
            await RefreshAsync();
            if (!_lifetime.IsClosing && _entries.GetValueOrDefault(key) is { } added) Finish(added);
        }
        catch (OperationCanceledException) when (_lifetime.IsClosing) { }
        catch (Exception error)
        {
            if (!_lifetime.IsClosing) ReportFailure(error is RuntimeException runtime ? runtime.Message
                : "That model could not be added. Check the file and folder access, then try again. Existing files were kept.");
        }
        finally { _shell.ClearModelActivity(); }
    }
}
