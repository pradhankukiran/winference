using System.Text.Json;

namespace Winference.Core.Media;

public enum MediaKind { SpeechInput, Voice, Image, Embeddings }

/// <summary>An optional model the bundled Lemonade server can download and run beside chat.</summary>
/// <param name="LemonadeModel">The name in Lemonade's model registry.</param>
/// <param name="SizeGb">Download size reported by Lemonade's registry.</param>
/// <param name="Engine">Lemonade's engine folder under cache\bin.</param>
/// <param name="RepoFolders">Hugging Face cache folders the model is stored in.</param>
/// <param name="Files">Model file names, including extras Lemonade's delete leaves behind.</param>
/// <param name="QueryPrefix">Text an embedding model expects before a search question.</param>
/// <param name="DocumentPrefix">Text an embedding model expects before a document passage.</param>
public sealed record MediaAddon(MediaKind Kind, string LemonadeModel, string Name, string Description, double SizeGb,
    string Engine, string[] RepoFolders, string[] Files, bool IsDefault = false,
    string QueryPrefix = "", string DocumentPrefix = "")
{
    public string SizeLabel => SizeGb < 1 ? $"{SizeGb * 1024:0} MB" : $"{SizeGb:0.#} GB";
}

public static class MediaAddons
{
    /// <summary>Kept small on purpose. Every entry was checked against Lemonade 2026.42.1's registry.</summary>
    public static IReadOnlyList<MediaAddon> All { get; } =
    [
        Whisper("Whisper-Base", "Whisper Base", "Fast speech recognition. Runs on the NPU when available.", 0.148, "base", isDefault: true),
        Whisper("Whisper-Small", "Whisper Small", "More accurate, a little slower.", 0.488, "small"),
        Whisper("Whisper-Large-v3-Turbo", "Whisper Large v3 Turbo", "Most accurate, including accents and other languages.", 1.62, "large-v3-turbo"),
        new(MediaKind.Voice, "kokoro-v1", "Kokoro", "Natural-sounding voices for Read aloud.", 0.354,
            "kokoro", ["models--mikkoph--kokoro-onnx"], ["kokoro-v1.0.onnx"], IsDefault: true),
        new(MediaKind.Image, "SD-Turbo-GGUF", "SD Turbo", "Quick 512 × 512 images in a few seconds on the GPU.", 2.02,
            "sd-cpp", ["models--Green-Sky--SD-Turbo-GGUF"], ["sd_turbo-f16-q8_0.gguf"], IsDefault: true),
        new(MediaKind.Image, "SDXL-Turbo", "SDXL Turbo", "Better detail. Needs more memory and time.", 6.94,
            "sd-cpp", ["models--stabilityai--sdxl-turbo"], ["sd_xl_turbo_1.0_fp16.safetensors"]),
        new(MediaKind.Embeddings, "nomic-embed-text-v1-GGUF", "Nomic Embed v1", "Finds the right parts of your documents by meaning, not just matching words.", 0.0781,
            "llamacpp", ["models--nomic-ai--nomic-embed-text-v1-GGUF"], ["nomic-embed-text-v1.Q4_K_S.gguf"], IsDefault: true,
            QueryPrefix: "search_query: ", DocumentPrefix: "search_document: "),
        new(MediaKind.Embeddings, "Qwen3-Embedding-0.6B-GGUF", "Qwen3 Embedding 0.6B", "More accurate document search, including other languages.", 0.64,
            "llamacpp", ["models--Qwen--Qwen3-Embedding-0.6B-GGUF"], ["Qwen3-Embedding-0.6B-Q8_0.gguf"],
            QueryPrefix: "Instruct: Given a question, find the passages that answer it\nQuery: "),
    ];

    // All Whisper sizes share one repository, and the NPU encoder lands beside the main file.
    private static MediaAddon Whisper(string model, string name, string description, double size, string variant, bool isDefault = false) =>
        new(MediaKind.SpeechInput, model, name, description, size, "whispercpp",
            ["models--ggerganov--whisper.cpp", $"models--amd--whisper-{(variant == "large-v3-turbo" ? "large-turbo" : variant)}-onnx-npu"],
            [$"ggml-{variant}.bin", $"ggml-{variant}-encoder-vitisai.rai"], isDefault);

    /// <summary>
    /// Deletes what Lemonade's own delete leaves behind for a removed add-on: its files, model
    /// folders without any model data left, and its engine once no installed add-on uses it.
    /// </summary>
    public static void CleanUp(MediaAddon removed, IReadOnlySet<string> stillInstalled, string modelsDir, string engineDir)
    {
        foreach (var repo in removed.RepoFolders)
        {
            var folder = Path.Combine(modelsDir, repo);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray())
            {
                var name = Path.GetFileName(file);
                if (removed.Files.Any(owned => name.Equals(owned, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(owned + ".partial", StringComparison.OrdinalIgnoreCase))) File.Delete(file);
            }
            // Only metadata (registry, refs, readme) left means no other model is stored here.
            if (!Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(file => new FileInfo(file).Length >= 1024 * 1024))
                Directory.Delete(folder, recursive: true);
        }
        var engineInUse = All.Any(addon => addon != removed && addon.Engine == removed.Engine && stillInstalled.Contains(addon.LemonadeModel));
        var engine = Path.Combine(engineDir, removed.Engine);
        if (!engineInUse && Directory.Exists(engine)) Directory.Delete(engine, recursive: true);
    }

    public static IEnumerable<MediaAddon> OfKind(MediaKind kind) => All.Where(addon => addon.Kind == kind);

    public static MediaAddon? Find(string lemonadeModel) =>
        All.FirstOrDefault(addon => addon.LemonadeModel.Equals(lemonadeModel, StringComparison.OrdinalIgnoreCase));

    /// <summary>English Kokoro voices offered in Settings. The model ships more languages.</summary>
    public static IReadOnlyList<(string Id, string Label)> Voices { get; } =
    [
        ("af_heart", "Heart (US, female)"),
        ("af_bella", "Bella (US, female)"),
        ("am_michael", "Michael (US, male)"),
        ("am_adam", "Adam (US, male)"),
        ("bf_emma", "Emma (UK, female)"),
        ("bm_george", "George (UK, male)"),
    ];

    public const string DefaultVoice = "af_heart";
}

/// <summary>Download progress across all files of one model.</summary>
public sealed record MediaDownloadProgress(long BytesDone, long BytesTotal, string File, bool Complete)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : Complete ? 1 : 0;
}

/// <summary>
/// Reads Lemonade's pull stream: "event: progress|complete|error" lines, each followed by a
/// "data: {json}" line. Per-file byte counts are turned into an overall total.
/// </summary>
public sealed class PullStreamParser
{
    private string _event = "message";
    private readonly Dictionary<int, long> _doneByFile = [];
    private long _total;

    /// <summary>Returns progress for a data line, null for other lines. Throws on an error event.</summary>
    public MediaDownloadProgress? Feed(string line)
    {
        if (line.StartsWith("event:", StringComparison.Ordinal))
        {
            _event = line[6..].Trim();
            return null;
        }
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var json = line[5..].Trim();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (_event == "error" || root.TryGetProperty("error", out _))
        {
            var message = root.TryGetProperty("error", out var error)
                ? error.ValueKind == JsonValueKind.String ? error.GetString() : error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText()
                : json;
            throw new Runtime.RuntimeException("media_download", "The download failed: " + message);
        }
        if (root.TryGetProperty("total_download_size", out var totalSize) && totalSize.GetInt64() > 0) _total = totalSize.GetInt64();
        var index = root.TryGetProperty("file_index", out var fileIndex) ? fileIndex.GetInt32() : 1;
        var bytes = root.TryGetProperty("bytes_downloaded", out var downloaded) ? downloaded.GetInt64() : 0;
        var file = root.TryGetProperty("file", out var name) ? name.GetString() ?? string.Empty : string.Empty;
        var complete = _event == "complete";
        if (!complete && file.Length > 0) _doneByFile[index] = Math.Max(_doneByFile.GetValueOrDefault(index), bytes);
        var done = complete ? _total : _doneByFile.Values.Sum();
        return new(done, _total, file, complete);
    }
}
