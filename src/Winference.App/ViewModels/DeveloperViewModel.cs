using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Winference.App.Services;
using Winference.Infrastructure.Developer;
using Winference.Infrastructure.Selection;

namespace Winference.App.ViewModels;

/// <summary>The Developer page: the local server switch, which apps may use it, and what they asked.</summary>
public sealed class DeveloperViewModel : INotifyPropertyChanged
{
    private const int MaxLogEntries = 200;
    private readonly LocalApiHost _host;
    private readonly AppPermissionStore _permissions;
    private readonly AppPreferencesStore _preferences;
    private readonly DispatcherQueue _dispatcher;
    private bool _isOn;
    private int _port;
    private string? _error;
    private bool _changing;

    internal DeveloperViewModel(LocalApiHost host, AppPermissionStore permissions, AppPreferencesStore preferences, DispatcherQueue dispatcher)
    {
        _host = host;
        _permissions = permissions;
        _preferences = preferences;
        _dispatcher = dispatcher;
        var saved = preferences.Read();
        _port = saved.ServerPort is > 0 and <= 65535 ? saved.ServerPort : AppPreferences.DefaultServerPort;
        _isOn = saved.ServerEnabled;
        host.Logged += (_, entry) => _dispatcher.TryEnqueue(() => AddLog(entry));
        permissions.Changed += (_, _) => _dispatcher.TryEnqueue(RefreshApps);
        Requests.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRequests));
            OnPropertyChanged(nameof(NoRequests));
        };
        RefreshApps();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AppPermissionItem> Apps { get; } = [];
    public ObservableCollection<RequestLogItem> Requests { get; } = [];
    public bool HasApps => Apps.Count > 0;
    public bool NoApps => Apps.Count == 0;
    public bool HasRequests => Requests.Count > 0;
    public bool NoRequests => Requests.Count == 0;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            OnPropertyChanged();
            _ = ApplyAsync();
        }
    }

    public double Port
    {
        get => _port;
        set
        {
            if (!double.IsFinite(value)) return;
            var port = (int)Math.Clamp(Math.Round(value), 1024, 65535);
            if (_port == port) return;
            _port = port;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Address));
            _preferences.Write(_preferences.Read() with { ServerPort = port });
            if (_isOn) _ = ApplyAsync();
        }
    }

    public string Address => $"http://127.0.0.1:{_port}/v1";

    public string Status => _error is not null ? "Not running" : _host.IsRunning ? $"Running on port {_host.Port}. Only apps on this PC can reach it." : "Off";

    public string? Error
    {
        get => _error;
        private set
        {
            _error = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(Status));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>Starts the server at launch if the user left it on.</summary>
    public Task StartIfOnAsync() => _isOn ? ApplyAsync() : Task.CompletedTask;

    internal Task ShutdownAsync() => _host.StopAsync();

    public void SetAllowed(AppPermissionItem item, bool allowed)
    {
        if (_permissions.Find(item.Key) is not { } saved || saved.Allowed == allowed) return;
        _permissions.Set(saved with { Allowed = allowed, DecidedAt = DateTimeOffset.Now });
    }

    public void Forget(AppPermissionItem item) => _permissions.Remove(item.Key);

    public void ClearLog() => Requests.Clear();

    private async Task ApplyAsync()
    {
        if (_changing) return;
        _changing = true;
        try
        {
            // Settle on the latest choice: the switch or port may change while the server moves.
            int port;
            bool on;
            do
            {
                port = _port;
                on = _isOn;
                _preferences.Write(_preferences.Read() with { ServerEnabled = on });
                Error = on ? await _host.StartAsync(port) : null;
                if (!on) await _host.StopAsync();
            } while (port != _port || on != _isOn);
        }
        finally
        {
            _changing = false;
            OnPropertyChanged(nameof(Status));
        }
    }

    private void RefreshApps()
    {
        Apps.Clear();
        foreach (var app in _permissions.All()) Apps.Add(new AppPermissionItem(app, this));
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(NoApps));
    }

    private void AddLog(ApiLogEntry entry)
    {
        Requests.Insert(0, new RequestLogItem(entry));
        while (Requests.Count > MaxLogEntries) Requests.RemoveAt(Requests.Count - 1);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>One app in the Developer page's list. Turning the switch saves the new answer.</summary>
public sealed class AppPermissionItem(AppPermission app, DeveloperViewModel owner) : INotifyPropertyChanged
{
    private bool _allowed = app.Allowed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key => app.Key;
    public string Name => app.Name;
    public string Detail => app.Path ?? "Program location unknown";
    public string LastUsed => app.LastUsed is { } used ? "Last used " + used.ToLocalTime().ToString("g") : app.Allowed ? "Not used yet" : "Blocked";
    public string ForgetLabel => "Forget " + app.Name;
    public string SwitchLabel => "Allow " + app.Name;

    public bool IsAllowed
    {
        get => _allowed;
        set
        {
            if (_allowed == value) return;
            _allowed = value;
            PropertyChanged?.Invoke(this, new(nameof(IsAllowed)));
            owner.SetAllowed(this, value);
        }
    }
}

public sealed class RequestLogItem(ApiLogEntry entry)
{
    public string Time => entry.Time.ToLocalTime().ToString("T");
    public string App => entry.App;
    public string Request => entry.Request;
    public string Result => entry.Result;
    public bool IsProblem => entry.IsProblem;
    public string AutomationName => $"{Time}, {App}: {Request}. {Result}";
}
