using System.Text.Json;

namespace Winference.Infrastructure.Developer;

/// <summary>The user's answer for one app that asked to use the local server.</summary>
public sealed record AppPermission(string Key, string Name, string? Path, bool Allowed, DateTimeOffset DecidedAt, DateTimeOffset? LastUsed = null);

/// <summary>
/// Which apps may use the local server. Losing the file only means each app asks again,
/// so a damaged file is treated as empty rather than as permission.
/// </summary>
public sealed class AppPermissionStore(string dataRoot)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(Path.GetFullPath(dataRoot), "developer-apps.json");
    private readonly object _sync = new();
    private List<AppPermission>? _apps;

    public event EventHandler? Changed;

    public IReadOnlyList<AppPermission> All()
    {
        lock (_sync) return [.. Load().OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public AppPermission? Find(string key)
    {
        lock (_sync) return Load().FirstOrDefault(app => app.Key == key);
    }

    public void Set(AppPermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        lock (_sync)
        {
            var apps = Load();
            apps.RemoveAll(app => app.Key == permission.Key);
            apps.Add(permission);
            Save(apps);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string key)
    {
        lock (_sync)
        {
            var apps = Load();
            if (apps.RemoveAll(app => app.Key == key) == 0) return;
            Save(apps);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkUsed(string key, DateTimeOffset when)
    {
        lock (_sync)
        {
            var apps = Load();
            var index = apps.FindIndex(app => app.Key == key);
            if (index < 0) return;
            apps[index] = apps[index] with { LastUsed = when };
            Save(apps);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<AppPermission> Load()
    {
        if (_apps is not null) return _apps;
        try
        {
            _apps = File.Exists(_path)
                ? (JsonSerializer.Deserialize<List<AppPermission>>(File.ReadAllText(_path), Options) ?? [])
                    .Where(app => !string.IsNullOrWhiteSpace(app?.Key) && !string.IsNullOrWhiteSpace(app.Name)).ToList()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _apps = [];
        }
        return _apps;
    }

    private void Save(List<AppPermission> apps)
    {
        _apps = apps;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(apps, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The answer still applies until Winference closes.
        }
    }
}
