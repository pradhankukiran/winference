using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Lemonade;

/// <summary>
/// Universal client-side tokenizer that loads vocabulary, BPE merges, and pre-tokenization
/// regex patterns dynamically from any Hugging Face tokenizer.json and tokenizer_config.json.
/// Operates in memory on the CPU for sub-millisecond context window validation across all models.
/// </summary>
public sealed class LocalModelTokenizer : IDisposable
{
    private enum TemplateStyle { Phi, Llama3, ChatML }

    private readonly BpeTokenizer _tokenizer;
    private readonly Dictionary<string, AddedTokenRule> _addedTokens;
    private readonly Regex? _addedTokenPattern;
    private readonly TemplateStyle _templateStyle;
    private bool _disposed;

    public LocalModelTokenizer(string tokenizerJsonPath, string? tokenizerConfigPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerJsonPath);
        if (!File.Exists(tokenizerJsonPath))
            throw new FileNotFoundException("Model tokenizer file was not found.", tokenizerJsonPath);

        using var tokenizerDocument = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
        var root = tokenizerDocument.RootElement;
        if (!root.TryGetProperty("model", out var model) ||
            !model.TryGetProperty("type", out var modelType) ||
            modelType.GetString() != "BPE")
        {
            throw new RuntimeException("tokenizer_unsupported", "Only BPE-based model tokenizers are supported for local counting.");
        }

        // Dynamically extract the pre-tokenizer regex pattern from tokenizer.json if present
        var splitRegex = ExtractSplitPattern(root);

        // Load vocabulary
        var vocabulary = model.GetProperty("vocab").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetInt32(), StringComparer.Ordinal);

        // Load merges
        var merges = new List<string>();
        if (model.TryGetProperty("merges", out var mergesElement) && mergesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in mergesElement.EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                    merges.Add(pair[0].GetString() + " " + pair[1].GetString());
                else if (pair.ValueKind == JsonValueKind.String)
                    merges.Add(pair.GetString()!);
            }
        }

        // Load special / added tokens with their strip rules
        _addedTokens = new Dictionary<string, AddedTokenRule>(StringComparer.Ordinal);
        if (root.TryGetProperty("added_tokens", out var addedElement) && addedElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in addedElement.EnumerateArray())
            {
                if (token.TryGetProperty("content", out var contentElem) && contentElem.GetString() is { Length: > 0 } content)
                {
                    var lstrip = token.TryGetProperty("lstrip", out var l) && l.GetBoolean();
                    var rstrip = token.TryGetProperty("rstrip", out var r) && r.GetBoolean();
                    _addedTokens[content] = new AddedTokenRule(lstrip, rstrip);
                }
            }
        }

        if (_addedTokens.Count > 0)
        {
            _addedTokenPattern = new Regex(
                string.Join("|", _addedTokens.Keys.OrderByDescending(x => x.Length).Select(Regex.Escape)),
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
    }
}
