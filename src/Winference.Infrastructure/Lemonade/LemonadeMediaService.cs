using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winference.Core.Media;
using Winference.Core.Runtime;
using Winference.Infrastructure.Setup;
using Winference.Infrastructure.Windows;

namespace Winference.Infrastructure.Lemonade;

/// <summary>
/// A second, app-owned Lemonade server for speech input, natural voices and images.
/// Unlike the NPU chat server it may download: models from Hugging Face and engines from
/// Lemonade's pinned, checksum-verified GitHub releases. It listens on loopback with a
/// random key, starts on first use and stops after a few idle minutes.
/// </summary>
public sealed class LemonadeMediaService : IAsyncDisposable
{
    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(5);
    private const string Version = "2026.42.1";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _lemonadePath;
    private readonly string _root;
    private readonly string _installedPath;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _sync = new();
    private OwnedProcessSession? _owned;
    private HttpClient? _http;
    private int _busy;
    private CancellationTokenSource? _idle;

    public LemonadeMediaService(string lemonadePath, string dataRoot)
    {
        _lemonadePath = Path.GetFullPath(lemonadePath);
        _root = Path.Combine(Path.GetFullPath(dataRoot), "Media");
        _installedPath = Path.Combine(_root, "installed.json");
    }

    public event EventHandler? InstalledChanged;

    /// <summary>Models this app finished downloading and setting up. Read without starting the server.</summary>
    public IReadOnlySet<string> Installed
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return File.Exists(_installedPath)
                        ? (JsonSerializer.Deserialize<string[]>(File.ReadAllText(_installedPath)) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }
}
