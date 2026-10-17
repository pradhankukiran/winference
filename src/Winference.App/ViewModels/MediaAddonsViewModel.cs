using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Winference.App.Diagnostics;
using Winference.App.Services;
using Winference.Core.Media;
using Winference.Core.Runtime;

namespace Winference.App.ViewModels;

/// <summary>The Settings list of optional Lemonade add-ons: speech input, natural voices and images.</summary>
public sealed class MediaAddonsViewModel
{
    private readonly MediaSelection _selection;
    private readonly Func<string, string[], string, bool, Task<bool>> _confirm;
    private readonly Func<string, string[], Task> _inform;

    internal MediaAddonsViewModel(MediaSelection selection, Func<string, string[], string, bool, Task<bool>> confirm, Func<string, string[], Task> inform)
    {
        _selection = selection;
        _confirm = confirm;
        _inform = inform;
        foreach (var addon in MediaAddons.All) Items.Add(new MediaAddonItem(addon, this));
        selection.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<MediaAddonItem> Items { get; } = [];
    public IReadOnlyList<(string Id, string Label)> Voices => MediaAddons.Voices;
    public string Voice => _selection.Voice;
    public bool HasVoiceAddon => _selection.ActiveModel(MediaKind.Voice) is not null;
    public event EventHandler? Changed;

    public void SetVoice(string voice) => _selection.SetVoice(voice);

    private void Refresh()
    {
        var installed = _selection.Service.Installed;
        var active = Enum.GetValues<MediaKind>().Select(_selection.ActiveModel).ToHashSet();
        foreach (var item in Items) item.Update(installed.Contains(item.Addon.LemonadeModel), active.Contains(item.Addon.LemonadeModel));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal async Task ActAsync(MediaAddonItem item)
    {
        if (item.Cancellation is { } running) { running.Cancel(); return; }
        if (item.IsInstalled && !item.IsActive && item.Addon.Kind != MediaKind.Voice)
        {
            _selection.Prefer(item.Addon);
            return;
        }
        if (item.IsInstalled)
        {
            if (!await _confirm($"Remove {item.Addon.Name}?", [$"This frees {item.Addon.SizeLabel}. You can get it again later."], "Remove", false)) return;
            await RunAsync(item, token => _selection.Service.RemoveAsync(item.Addon.LemonadeModel, token), "Removing…");
            return;
        }
        var what = item.Addon.Kind switch
        {
            MediaKind.SpeechInput => "Talk to Winference with the microphone button in the message box.",
            MediaKind.Voice => "Read aloud uses this voice instead of the built-in Windows voice.",
            MediaKind.Embeddings => "Questions about attached documents find the right passages by meaning, even when the words differ.",
            _ => "Type /image followed by a description in the message box to create a picture."
        };
        if (!await _confirm($"Get {item.Addon.Name}?",
            [what, $"Downloads {item.Addon.SizeLabel} from Hugging Face, plus its engine from Lemonade's verified GitHub releases. Everything then runs on this PC."],
            "Download", true)) return;
        await RunAsync(item, async token =>
        {
            var progress = new Progress<MediaDownloadProgress>(update => item.ReportProgress(update));
            var stage = new Progress<string>(text => item.Detail = text == "Setting up" ? "Setting up the engine…" : "Starting download…");
            await _selection.Service.InstallAsync(item.Addon.LemonadeModel, progress, stage, token);
            if (item.Addon.Kind != MediaKind.Voice) _selection.Prefer(item.Addon);
        }, "Starting…");
    }

    private async Task RunAsync(MediaAddonItem item, Func<CancellationToken, Task> work, string detail)
    {
        using var cancellation = new CancellationTokenSource();
        item.Begin(cancellation, detail);
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (RuntimeException error)
        {
            await _inform($"{item.Addon.Name} didn't finish", [error.Message]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            AppDiagnostics.WriteException("media.addon.failed", error);
            await _inform($"{item.Addon.Name} didn't finish", ["Check your internet connection and free disk space, then try again."]);
        }
        finally
        {
            item.End();
            Refresh();
        }
    }
}

public sealed class MediaAddonItem : INotifyPropertyChanged
{
    private readonly MediaAddonsViewModel _owner;
    private string _detail = string.Empty;
    private double _progress;
    private bool _indeterminate;

    internal MediaAddonItem(MediaAddon addon, MediaAddonsViewModel owner)
    {
        Addon = addon;
        _owner = owner;
    }

    public MediaAddon Addon { get; }
    public string Name => Addon.Name;
    public string Description => Addon.Description;
    public string Meta => (Addon.Kind switch
    {
        MediaKind.SpeechInput => "Speech input",
        MediaKind.Voice => "Voice",
        MediaKind.Embeddings => "Document search",
        _ => "Images"
    }) + " · " + Addon.SizeLabel + (IsActive ? " · In use" : IsInstalled ? " · Installed" : string.Empty);
    public bool IsInstalled { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsBusy => Cancellation is not null;
    internal CancellationTokenSource? Cancellation { get; private set; }
    public string ActionLabel => IsBusy ? "Cancel" : !IsInstalled ? "Get" : IsActive || Addon.Kind == MediaKind.Voice ? "Remove" : "Use";
    public string ActionName => ActionLabel + " " + Name;
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public double Progress { get => _progress; private set { _progress = value; OnPropertyChanged(); } }
    public bool IsIndeterminate { get => _indeterminate; private set { _indeterminate = value; OnPropertyChanged(); } }

    public string Detail
    {
        get => _detail;
        set
        {
            _detail = value;
            if (value.StartsWith("Setting up", StringComparison.Ordinal) || value.EndsWith('…')) IsIndeterminate = true;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task ActAsync() => _owner.ActAsync(this);

    internal void Update(bool installed, bool active)
    {
        IsInstalled = installed;
        IsActive = active;
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionName));
    }

    internal void Begin(CancellationTokenSource cancellation, string detail)
    {
        Cancellation = cancellation;
        Progress = 0;
        Detail = detail;
        RaiseBusy();
    }

    internal void End()
    {
        Cancellation = null;
        Detail = string.Empty;
        IsIndeterminate = false;
        RaiseBusy();
    }

    internal void ReportProgress(MediaDownloadProgress update)
    {
        if (update.BytesTotal <= 0) return;
        IsIndeterminate = false;
        Progress = update.Fraction * 100;
        _detail = $"Downloading… {update.BytesDone / 1048576.0:0} of {update.BytesTotal / 1048576.0:0} MB";
        OnPropertyChanged(nameof(Detail));
    }

    private void RaiseBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(BusyVisibility));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionName));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
