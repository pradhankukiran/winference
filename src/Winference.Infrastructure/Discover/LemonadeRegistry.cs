using System.Text.Json;
using System.Text.RegularExpressions;
using Winference.Core.Discover;
using Winference.Core.Models;

namespace Winference.Infrastructure.Discover;

/// <summary>
/// One tested model from Lemonade's registry. GPU entries name a Hugging Face GGUF repository and
/// either an exact file or a quantization to pick; GPU + NPU entries name an AMD export repository.
/// </summary>
public sealed record RegistryModel(
    string Name, ExecutionMode Mode, string Repository, string? FileName, string? Quantization,
    long? SizeBytes, ModelCapabilities Capabilities, bool Suggested, bool HasVisionProjector)
{
    public string FamilyKey => ModelFamilyKey.For(Name);
    public string SourcePage => "https://huggingface.co/" + Repository;
}

/// <summary>
/// Reads Lemonade's model registry and keeps only chat models our pinned engines can run:
/// Hugging Face GGUF files for llama.cpp, and AMD exports built for Ryzen AI 1.7.
/// </summary>
public static partial class LemonadeRegistry
{
    /// <summary>GPU + NPU exports must be built for the Ryzen AI version this app pins.</summary>
    public const string HybridMarker = "ryzenai-1.7-hybrid";

    private static readonly HashSet<string> ChatLabels = new(StringComparer.OrdinalIgnoreCase)
        { "chat", "reasoning", "coding", "vision", "tool-calling" };
    private static readonly HashSet<string> OtherUses = new(StringComparer.OrdinalIgnoreCase)
        { "embeddings", "reranking", "transcription", "tts", "image", "audio-generation", "speech" };

    public static IReadOnlyList<RegistryModel> Load(string path) => Parse(File.ReadAllBytes(path));

    public static IReadOnlyList<RegistryModel> Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var models = new List<RegistryModel>();
        if (document.RootElement.ValueKind != JsonValueKind.Object) return models;
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object) continue;
            var labels = Strings(value, "labels");
            if (!labels.Any(ChatLabels.Contains) || labels.Any(OtherUses.Contains)) continue;
            var recipe = Text(value, "recipe");
            var checkpoint = Text(value, "checkpoint");
            var source = Text(value, "source");
            if (checkpoint is null || source is not (null or "huggingface")) continue;
            var size = value.TryGetProperty("size", out var gb) && gb.TryGetDouble(out var sizeGb) && sizeGb > 0
                ? (long)(sizeGb * 1_000_000_000) : (long?)null;
            var capabilities = ModelCapabilityLabels.FromLabels(labels);
            var suggested = value.TryGetProperty("suggested", out var flag) && flag.ValueKind == JsonValueKind.True;

            if (recipe == "llamacpp")
            {
                var separator = checkpoint.IndexOf(':');
                if (separator <= 0) continue;
                var repository = checkpoint[..separator];
                var selector = checkpoint[(separator + 1)..];
                if (!IsRepository(repository) || selector.Length == 0 || selector.Contains('*') || selector.Contains('/')) continue;
                var isFile = selector.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
                models.Add(new(entry.Name, ExecutionMode.GpuOnly, repository, isFile ? selector : null, isFile ? null : selector,
                    size, capabilities, suggested, value.TryGetProperty("mmproj", out _)));
            }
            else if (recipe == "ryzenai-llm" && entry.Name.EndsWith("-Hybrid", StringComparison.Ordinal) &&
                checkpoint.EndsWith(HybridMarker, StringComparison.OrdinalIgnoreCase) && IsRepository(checkpoint))
            {
                models.Add(new(entry.Name, ExecutionMode.GpuNpu, checkpoint, null, null, size, capabilities, suggested, false));
            }
        }
        return models;
    }

    public static bool IsRepository(string value) => RepositoryPattern().IsMatch(value);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,95}/[A-Za-z0-9][A-Za-z0-9_.-]{0,95}$")]
    private static partial Regex RepositoryPattern();
}
