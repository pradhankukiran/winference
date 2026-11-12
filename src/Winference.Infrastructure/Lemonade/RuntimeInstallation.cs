using System.Security.Cryptography;
using System.Text.Json;
using Winference.Core.Models;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Lemonade;

public sealed class RuntimeInstallation
{
    private readonly JsonElement _manifest;
    public string LemonadePath { get; }
    public string FlmPath { get; }
    public string ModelRoot { get; }
    public string SessionsRoot { get; }
    public string ModelDirectory
    {
        get
        {
            var candidate1 = Path.Combine(ModelRoot, "models", Get("modelDirectory"));
            if (Directory.Exists(candidate1)) return candidate1;
            var candidate2 = Path.Combine(ModelRoot, Get("modelDirectory"));
            if (Directory.Exists(candidate2)) return candidate2;
            return candidate1;
        }
    }
    public string Checkpoint => _manifest.GetProperty("checkpoint").GetString()!;
    public string LemonadeVersion => _manifest.GetProperty("lemonadeVersion").GetString()!;
    public string ModelName => _manifest.TryGetProperty("modelName", out var name) ? name.GetString()! : Checkpoint;
    public string ModelPublisher => _manifest.TryGetProperty("publisher", out var publisher) ? publisher.GetString()! : "Local model";
    public string TokenizerPolicy => _manifest.TryGetProperty("tokenizerPolicy", out var policy) ? policy.GetString()! : "runtime";
    public ModelProfile Profile { get; }

    public RuntimeInstallation(string manifestPath, string lemonadePath, string flmPath, string modelRoot, string sessionsRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        _manifest = document.RootElement.Clone();
        if (_manifest.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unknown runtime profile version.");
        LemonadePath = Path.GetFullPath(lemonadePath); FlmPath = Path.GetFullPath(flmPath);
        ModelRoot = Path.GetFullPath(modelRoot); SessionsRoot = Path.GetFullPath(sessionsRoot);
        Profile = new ModelProfile(Get("modelFamilyId"), Get("profileId"), Get("artifactId"),
            Get("providerModelId"), ExecutionMode.NpuOnly, _manifest.GetProperty("contextLength").GetInt32(),
            ReplyReserve(_manifest.GetProperty("contextLength").GetInt32()),
            SupportsImages: _manifest.TryGetProperty("supportsImages", out var images) && images.ValueKind == JsonValueKind.True);
    }

    /// <summary>
    /// Room always kept free for a reply. A reply may use more when the conversation leaves it.
    /// Older manifests saved a 256-token cap; it is replaced here so they need no rewrite.
    /// </summary>
    public static int ReplyReserve(int contextLength) => Math.Min(512, contextLength / 4);

    public async Task<RuntimeAvailability> VerifyAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "NPU chat requires Windows.", "windows_required");
        if (profile != Profile) return new(false, "This model profile has not been qualified.", "profile_mismatch");
        if (!File.Exists(LemonadePath)) return new(false, "The Lemonade runtime is missing. Restore the complete Winference application folder.", "missing_runtime");
        if (!File.Exists(FlmPath)) return new(false, "An AI component is missing. Return to model setup to restore it.", "missing_flm");
        if (!await MatchesAsync(LemonadePath, Get("lemonadeExecutableSha256"), cancellationToken).ConfigureAwait(false) ||
            !await MatchesAsync(FlmPath, Get("flmExecutableSha256"), cancellationToken).ConfigureAwait(false))
            return new(false, "The AI components differ from the verified setup. Return to model setup to repair them.", "runtime_mismatch");
        var directory = ModelDirectory;
        foreach (var file in _manifest.GetProperty("modelFiles").EnumerateArray())
        {
            var path = Path.Combine(directory, file.GetProperty("name").GetString()!);
            if (!File.Exists(path) || new FileInfo(path).Length != file.GetProperty("lengthBytes").GetInt64())
                return new(false, "The selected model is missing or incomplete. Return to model setup. No files have been downloaded.", "missing_model");
            if (!await MatchesAsync(path, file.GetProperty("sha256").GetString()!, cancellationToken).ConfigureAwait(false))
                return new(false, "The selected model files have changed since setup. Existing files were left alone. Return to model setup to verify them.", "artifact_mismatch");
        }
        return new(true, "The selected model files and verified AI components are available.");
    }

    public Dictionary<string, string> CreateEnvironment(string apiKey)
    {
        var result = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(x => x.Key is string key && !key.StartsWith("LEMONADE_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => (string)x.Key, x => (string)x.Value!, StringComparer.OrdinalIgnoreCase);
        result["PATH"] = Path.GetDirectoryName(FlmPath) + ";" + result.GetValueOrDefault("PATH", "");
        result["FLM_MODEL_PATH"] = ModelRoot;
        result["LEMONADE_API_KEY"] = apiKey;
        // The tagged backend resolver accepts this process-only path override.
        // It is needed for the Windows model-discovery path, which ignores
        // prefer_system. Its target was hash-qualified before process creation.
        result["LEMONADE_FLM_NPU_BIN"] = FlmPath;
        return result;
    }

    private string Get(string key) => _manifest.GetProperty(key).GetString()!;
    private static async Task<bool> MatchesAsync(string path, string hash, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)) == hash;
    }
}
