using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Winference.Core.Runtime;
using Winference.Infrastructure.Windows;

namespace Winference.App.ViewModels;

/// <summary>
/// The small panel shown while a reply is written: how busy the NPU and GPU are, the power
/// they draw and the writing speed. It reads once a second, and only while a reply runs.
/// </summary>
public sealed class ChipActivityViewModel : INotifyPropertyChanged
{
    private readonly ShellViewModel _shell;
    private readonly ReplyMeter _meter;
    private readonly DispatcherQueueTimer _timer;
    private readonly Lazy<ChipMonitor?> _monitor = new(ChipMonitor.TryCreate, LazyThreadSafetyMode.ExecutionAndPublication);
    private bool _sampling;
    private bool _visible;
    private int _quietTicks;
    private ChipSample? _sample;
    private double? _speed;

    public ChipActivityViewModel(ShellViewModel shell, ReplyMeter meter, DispatcherQueue dispatcher)
    {
        _shell = shell;
        _meter = meter;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsVisible => _visible;
    public bool IsMeasuring => _visible && _sample is null && _speed is null;

    public bool ShowNpu => _sample?.NpuBusy is not null;
    public string NpuText => Part("NPU", _sample?.NpuBusy, _sample?.NpuWatts);
    public bool ShowGpu => _sample?.GpuBusy is not null;
    public string GpuText => Part("GPU", _sample?.GpuBusy, _sample?.GpuWatts);
    public bool ShowChip => _sample?.ChipWatts is not null;
    public string ChipText => _sample?.ChipWatts is { } watts ? $"Chip {watts:0.0} W" : string.Empty;
    public bool ShowSpeed => _speed is not null;
    public string SpeedText => _speed is { } speed ? $"{speed:0.0} tokens/s" : string.Empty;

    public string Summary => !_visible ? string.Empty : IsMeasuring ? "Measuring chip activity"
        : string.Join(", ", new[] { ShowNpu ? NpuText : null, ShowGpu ? GpuText : null, ShowChip ? ChipText : null, ShowSpeed ? SpeedText : null }
            .Where(part => part is not null));

    private async void Tick()
    {
        var active = _shell.Chat?.IsGenerating == true || _meter.IsActive;
        if (!active)
        {
            // A short pause between tool rounds shouldn't make the panel flicker.
            if (_visible && ++_quietTicks >= 2)
            {
                _visible = false;
                _sample = null;
                _speed = null;
                Changed();
            }
            return;
        }
        _quietTicks = 0;
        if (!_visible)
        {
            _visible = true;
            Changed();
        }
        if (_sampling) return;
        _sampling = true;
        try
        {
            var sample = await Task.Run(() => _monitor.Value?.Sample());
            if (!_visible) return;
            if (sample is not null) _sample = sample;
            _speed = _meter.TokensPerSecond ?? _speed;
            Changed();
        }
        catch (Exception error)
        {
            Diagnostics.AppDiagnostics.WriteException("chip.sample.failed", error);
        }
        finally { _sampling = false; }
    }

    private static string Part(string label, double? busy, double? watts) =>
        busy is not { } value ? string.Empty : watts is { } power ? $"{label} {value:0}% · {power:0.0} W" : $"{label} {value:0}%";

    private void Changed()
    {
        foreach (var name in new[] { nameof(IsVisible), nameof(IsMeasuring), nameof(ShowNpu), nameof(NpuText), nameof(ShowGpu),
            nameof(GpuText), nameof(ShowChip), nameof(ChipText), nameof(ShowSpeed), nameof(SpeedText), nameof(Summary) })
            OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
