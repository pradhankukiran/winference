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
}
