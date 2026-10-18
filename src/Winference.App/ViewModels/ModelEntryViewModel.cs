using System.ComponentModel;
using Microsoft.UI.Xaml;
using Winference.Core.Downloads;
using Winference.Core.Gpu;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Setup;
using Winference.Infrastructure.Models;

namespace Winference.App.ViewModels;

public enum ModelAction { None, Use, Get, Cancel, Repair }

/// <summary>One row in the model list. It stays the same object across refreshes so a download keeps its progress.</summary>
public sealed class ModelEntryViewModel : INotifyPropertyChanged
{
    private ModelStatus _status;
    private string? _failure;
    private string? _downloadMessage;
    private double? _percent;
    private bool _canAct = true;

    internal ModelEntryViewModel(ModelCatalogEntry entry, GpuModelRegistration? gpu, HybridModelRegistration? hybrid,
        SetupModel? npu, GpuCatalogModel? download, bool canRemove) => Update(entry, gpu, hybrid, npu, download, canRemove);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ModelCatalogEntry Entry { get; private set; } = null!;
    internal GpuModelRegistration? Gpu { get; private set; }
    internal HybridModelRegistration? Hybrid { get; private set; }
    internal SetupModel? Npu { get; private set; }
    internal GpuCatalogModel? Download { get; private set; }
    internal string Key => Entry.Profile.ProfileId;
    internal bool CanRemove { get; private set; }

    /// <summary>The identity the loaded chat reports, which differs from the library's profile ID.</summary>
    internal string? RuntimeModelId => Gpu?.ModelId ?? Hybrid?.ModelId ?? Npu?.Id;

    public string Name => ModelNames.Clean(Entry.Family.DisplayName);
    public string Publisher => Entry.Family.Publisher;
    public string Description => Entry.Family.Description;
    public string Badge => Entry.Profile.ModeLabel;
    public string SizeLabel => Entry.IsOnThisPc ? Size(Entry.Profile.StorageBytes) : Size(Entry.Profile.DownloadBytes ?? Entry.Profile.StorageBytes);

    public ModelStatus Status => _status;
    internal bool IsRecommended { get; private set; }
    public bool IsDownloading => _status == ModelStatus.Downloading;

    public string StatusLine => _status switch
    {
        ModelStatus.Downloading => _downloadMessage ?? "Downloading…",
        ModelStatus.Loading => "Loading…",
        ModelStatus.Current => "In use",
        ModelStatus.Failed => _failure ?? Entry.Profile.Compatibility.Label,
        ModelStatus.Ready => $"On this PC · {SizeLabel}",
        _ => IsRecommended ? $"Recommended · {SizeLabel}" : SizeLabel
    };

    /// <summary>The one line under a model's name: where it runs, then its state.</summary>
    public string Subtitle => $"{Badge} · {StatusLine}";
    public Visibility InUseVisibility => Visible(_status == ModelStatus.Current);

    public ModelAction Action => _status switch
    {
        ModelStatus.NotInstalled => ModelAction.Get,
        ModelStatus.Ready => ModelAction.Use,
        ModelStatus.Downloading => ModelAction.Cancel,
        ModelStatus.Failed => Entry.NeedsRepair ? ModelAction.Repair : Entry.IsOnThisPc ? ModelAction.Use : ModelAction.Get,
        _ => ModelAction.None
    };
}
