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

    public string ActionLabel => Action switch
    {
        ModelAction.Get => "Get",
        ModelAction.Use => _status == ModelStatus.Failed ? "Retry" : "Use",
        ModelAction.Cancel => "Cancel",
        ModelAction.Repair => "Find file",
        _ => string.Empty
    };

    public bool CanAct => _canAct && Action != ModelAction.None;
    public Visibility ActionVisibility => Visible(Action != ModelAction.None);
    public Visibility ProgressVisibility => Visible(IsDownloading);
    public bool IsProgressIndeterminate => _percent is null;
    public double ProgressValue => _percent ?? 0;
    public Visibility RemoveVisibility => Visible(CanRemove);
    public string ActionAutomationName => $"{ActionLabel} {Name} on {Badge}";
    public string DetailsAutomationName => $"Details for {Name} on {Badge}";
    public string RemoveAutomationName => $"Remove {Name} on {Badge}";

    public string DetailsText => string.Join(Environment.NewLine, new[]
    {
        $"Runs on {Badge}",
        Entry.Profile.Artifact.FormatLabel + " · " + Entry.Profile.Artifact.Quantization,
        Entry.Profile.Compatibility.Label,
        Entry.Profile.InstalledPath,
        Entry.Profile.Evidence
    }.Where(line => !string.IsNullOrWhiteSpace(line)));

    internal void Update(ModelCatalogEntry entry, GpuModelRegistration? gpu, HybridModelRegistration? hybrid,
        SetupModel? npu, GpuCatalogModel? download, bool canRemove)
    {
        Entry = entry;
        Gpu = gpu;
        Hybrid = hybrid;
        Npu = npu;
        Download = download;
        CanRemove = canRemove;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    internal void SetStatus(ModelStatus status, bool canAct, string? failure = null)
    {
        if (_status == status && _canAct == canAct && _failure == failure) return;
        _status = status;
        _canAct = canAct;
        _failure = failure;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    internal void SetRecommended(bool recommended)
    {
        IsRecommended = recommended;
        PropertyChanged?.Invoke(this, new(nameof(StatusLine)));
        PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
    }

    internal void ReportDownload(string message, double? percent)
    {
        _downloadMessage = percent is { } value ? $"{message} {value:0}%" : message;
        _percent = percent;
        PropertyChanged?.Invoke(this, new(nameof(StatusLine)));
        PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
        PropertyChanged?.Invoke(this, new(nameof(IsProgressIndeterminate)));
        PropertyChanged?.Invoke(this, new(nameof(ProgressValue)));
    }

    private static string Size(long? bytes) => bytes is > 0 ? StorageCheck.Format(bytes.Value) : "Size unavailable";
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    // ListViewItem exposes the item's ToString() as its automation name.
    public override string ToString() => $"{Name} · {Badge} · {StatusLine}";
}
