using System.IO;
using System.Text.Json;

namespace Winference.Infrastructure.Setup;

/// <summary>Explicit paths make first-run checks testable without touching user state.</summary>
public sealed record SetupPaths(
    string DataRoot, string LemonadePath, string CatalogPath, string ModelInfoPath,
    IReadOnlyList<string> RuntimeCandidates, IReadOnlyList<string> ModelRoots)
{
    public string SettingsPath => Path.Combine(DataRoot, "setup.json");
    public string DefaultModelStore => Path.Combine(DataRoot, "Models", "flm");
    public string RuntimeDirectory => Path.Combine(DataRoot, "Runtimes", "fastflowlm-1.0.10");
    public string SessionsRoot => Path.Combine(DataRoot, "Sessions");

    public static SetupPaths CreateDefault()
    {
        var requestedRoot = Environment.GetEnvironmentVariable("WINFERENCE_DATA_ROOT");
        var dataRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(requestedRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winference")
            : requestedRoot);
        var roots = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var value = Environment.GetEnvironmentVariable("FLM_MODEL_PATH", target);
            if (!string.IsNullOrWhiteSpace(value)) roots.Add(Path.GetFullPath(value));
        }
        var setupPath = Path.Combine(dataRoot, "setup.json");
        if (File.Exists(setupPath))
        {
            try
            {
                using var stream = File.OpenRead(setupPath);
                using var doc = System.Text.Json.JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("modelRoot", out var rootProp) &&
                    rootProp.GetString() is { } configuredRoot && Directory.Exists(configuredRoot))
                {
                    roots.Add(Path.GetFullPath(configuredRoot));
                }
            }
            catch { }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".flm"));
        roots.Add(Path.Combine(dataRoot, "Models", "flm"));
        return new(dataRoot,
            Path.Combine(AppContext.BaseDirectory, "Runtimes", "Lemonade", "lemond.exe"),
            Path.Combine(AppContext.BaseDirectory, "Catalogs", "catalog.json"),
            Path.Combine(AppContext.BaseDirectory, "Catalogs", "files.json"),
            [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "flm", "flm.exe")],
            roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

public sealed record SetupHardware(string Processor, string Graphics, string NeuralProcessor,
    long MemoryBytes, bool NpuDetected, string DriverStatus, string? BlockingReason);
