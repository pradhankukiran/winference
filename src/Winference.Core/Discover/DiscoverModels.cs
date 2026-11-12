namespace Winference.Core.Discover;

/// <summary>What a model can do beyond plain chat.</summary>
[Flags]
public enum ModelCapabilities { None = 0, Reasoning = 1, Vision = 2, Tools = 4, Coding = 8 }

/// <summary>Tested entries come from the engine makers' own lists; Community entries come from open search.</summary>
public enum CatalogTier { Tested, Community }

/// <summary>A rough answer to "will this run well here?". It is an estimate, not a measurement.</summary>
public enum FitLevel { Unknown, FitsWell, Tight, WontFit }

public static class ModelCapabilityLabels
{
    /// <summary>Reads the label words used by the FastFlowLM and Lemonade catalogs.</summary>
    public static ModelCapabilities FromLabels(IEnumerable<string>? labels)
    {
        var result = ModelCapabilities.None;
        foreach (var label in labels ?? [])
        {
            result |= label.Trim().ToLowerInvariant() switch
            {
                "reasoning" or "thinking" => ModelCapabilities.Reasoning,
                "vision" or "vlm" => ModelCapabilities.Vision,
                "tool-calling" or "tools" => ModelCapabilities.Tools,
                "coding" or "code" => ModelCapabilities.Coding,
                _ => ModelCapabilities.None
            };
        }
        return result;
    }
}

public static class ModelFit
{
    /// <summary>Memory Windows and everyday apps keep for themselves.</summary>
    public const long SystemReserveBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Compares a model's size, plus a share for its working memory, with the memory left after
    /// Windows. Up to 60% of that is a comfortable fit, up to 90% is tight, beyond that it won't fit.
    /// </summary>
    public static FitLevel Estimate(long? modelBytes, long memoryBytes)
    {
        if (modelBytes is not > 0 || memoryBytes <= 0) return FitLevel.Unknown;
        var usable = memoryBytes - SystemReserveBytes;
        if (usable <= 0) return FitLevel.WontFit;
        var needed = modelBytes.Value * 1.25;
        var share = needed / usable;
        return share <= 0.6 ? FitLevel.FitsWell : share <= 0.9 ? FitLevel.Tight : FitLevel.WontFit;
    }

    public static string Label(FitLevel fit) => fit switch
    {
        FitLevel.FitsWell => "Fits well",
        FitLevel.Tight => "Tight fit",
        FitLevel.WontFit => "Won't fit",
        _ => string.Empty
    };
}

/// <summary>
/// Groups builds of the same model across engines. "Llama-3.2-1B-NPU2", "Llama-3.2-1B-Instruct-GGUF"
/// and "Llama-3.2-1B-Instruct-Hybrid" all become "llama321b".
/// </summary>
public static class ModelFamilyKey
{
    private static readonly HashSet<string> PackagingWords = new(StringComparer.Ordinal)
    {
        "gguf", "hybrid", "npu", "npu2", "flm", "onnx", "ryzenai", "instruct", "it", "chat", "hf", "cpu",
        "quantized", "int4", "uint4", "awq", "bf16", "fp16", "float16", "ud"
    };

    public static string For(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = name.ToLowerInvariant()
            .Split(['-', '_', ':', '.', ' ', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !PackagingWords.Contains(word));
        var key = string.Concat(words.SelectMany(word => word.Where(char.IsAsciiLetterOrDigit)));
        return key.Length > 0 ? key : "model";
    }
}

/// <summary>
/// The company behind a model family. Download sources often name whoever repackaged a model
/// (for example "unsloth"), which isn't who made it.
/// </summary>
public static class ModelMakers
{
    private static readonly (string Prefix, string Maker)[] Known =
    [
        ("qwen", "Alibaba Qwen"), ("qwq", "Alibaba Qwen"), ("llama", "Meta"), ("codellama", "Meta"), ("gemma", "Google"),
        ("medgemma", "Google"), ("translategemma", "Google"), ("phi", "Microsoft"), ("mistral", "Mistral AI"),
        ("ministral", "Mistral AI"), ("devstral", "Mistral AI"), ("magistral", "Mistral AI"), ("deepseek", "DeepSeek"),
        ("gptoss", "OpenAI"), ("granite", "IBM"), ("lfm", "Liquid AI"), ("smollm", "Hugging Face"), ("olmo", "Ai2"),
        ("amdolmo", "AMD"), ("nemotron", "NVIDIA"), ("minicpm", "OpenBMB"), ("glm", "Zhipu AI"),
        ("hunyuan", "Tencent"), ("nanbeige", "Nanbeige"), ("kimi", "Moonshot AI"), ("exaone", "LG AI Research"),
        ("falcon", "TII"), ("cogito", "Deep Cogito"),
    ];

    /// <summary>The maker, or null when the name isn't recognised.</summary>
    public static string? For(string name)
    {
        var key = ModelFamilyKey.For(name);
        return Known.Where(known => key.StartsWith(known.Prefix, StringComparison.Ordinal))
            .OrderByDescending(known => known.Prefix.Length).Select(known => known.Maker).FirstOrDefault();
    }
}
