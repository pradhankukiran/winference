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

        // Detect chat template formatting
        _templateStyle = DetectTemplateStyle(tokenizerConfigPath);

        // Instantiate BPE tokenizer
        _tokenizer = BpeTokenizer.Create(new BpeOptions(vocabulary)
        {
            Merges = merges,
            ByteLevel = true,
            PreTokenizer = new ScalarRegexPreTokenizer(splitRegex)
        });
    }

    public int CountPromptTokens(IReadOnlyList<ChatMessage> messages)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count is < 1 or > 64)
            throw new RuntimeException("context_limit", "This chat is too long. Start a new chat before sending another message.");

        var text = RenderPrompt(messages);
        if (_addedTokenPattern is null)
            return _tokenizer.CountTokens(text);

        var count = 0;
        var position = 0;
        while (position < text.Length)
        {
            var match = _addedTokenPattern.Match(text, position);
            if (!match.Success)
            {
                if (position < text.Length)
                    count = checked(count + _tokenizer.CountTokens(text[position..]));
                break;
            }

            var rule = _addedTokens[match.Value];
            var ordinaryEnd = match.Index;
            if (rule.LeftStrip)
                while (ordinaryEnd > position && char.IsWhiteSpace(text[ordinaryEnd - 1])) --ordinaryEnd;

            if (ordinaryEnd > position)
                count = checked(count + _tokenizer.CountTokens(text[position..ordinaryEnd]));

            count = checked(count + 1);
            position = match.Index + match.Length;
            if (rule.RightStrip)
                while (position < text.Length && char.IsWhiteSpace(text[position])) ++position;
        }

        return count;
    }

    public void Validate(IReadOnlyList<ChatMessage> messages, int maxOutputTokens, int contextLength)
    {
        if (maxOutputTokens <= 0 || contextLength <= 1) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        var promptTokens = CountPromptTokens(messages);
        // Reserve an extra token for FastFlowLM prefill boundary
        if ((long)promptTokens + maxOutputTokens + 1 > contextLength)
            throw new RuntimeException("context_limit", $"This conversation exceeds the {contextLength:N0}-token context. Shorten your message or start a new chat.");
    }

    public void Dispose() => _disposed = true;

    private string RenderPrompt(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message is null || message.Role is not ("system" or "user" or "assistant") || message.Content is null)
                throw new RuntimeException("invalid_message", "The conversation contains an unsupported message.");
            EnsureValidUnicode(message.Content);
        }

        return _templateStyle switch
        {
            TemplateStyle.Llama3 => RenderLlama3(messages),
            TemplateStyle.ChatML => RenderChatML(messages),
            _ => RenderPhi(messages)
        };
    }

    private static string RenderPhi(IReadOnlyList<ChatMessage> messages)
    {
        var rendered = new StringBuilder();
        string? previousRole = null;
        foreach (var message in messages)
        {
            if (message.Role == previousRole && message.Role is "user" or "system")
            {
                rendered.Length -= "<|end|>".Length;
                rendered.Append(message.Content).Append("<|end|>");
            }
            else
            {
                rendered.Append("<|").Append(message.Role).Append("|>").Append(message.Content).Append("<|end|>");
            }
            previousRole = message.Role;
            if (rendered.Length > 262144)
                throw new RuntimeException("context_limit", "This message is too large. Shorten it or start a new chat.");
        }
        rendered.Append("<|assistant|>");
        return rendered.ToString();
    }

    private static string RenderLlama3(IReadOnlyList<ChatMessage> messages)
    {
        var rendered = new StringBuilder("<|begin_of_text|>");
        foreach (var message in messages)
        {
            rendered.Append("<|start_header_id|>").Append(message.Role).Append("<|end_header_id|>\n\n")
                    .Append(message.Content.Trim()).Append("<|eot_id|>");
            if (rendered.Length > 262144)
                throw new RuntimeException("context_limit", "This message is too large. Shorten it or start a new chat.");
        }
        rendered.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
        return rendered.ToString();
    }

    private static string RenderChatML(IReadOnlyList<ChatMessage> messages)
    {
        var rendered = new StringBuilder();
        foreach (var message in messages)
        {
            rendered.Append("<|im_start|>").Append(message.Role).Append('\n')
                    .Append(message.Content).Append("<|im_end|>\n");
            if (rendered.Length > 262144)
                throw new RuntimeException("context_limit", "This message is too large. Shorten it or start a new chat.");
        }
        rendered.Append("<|im_start|>assistant\n");
        return rendered.ToString();
    }

    private TemplateStyle DetectTemplateStyle(string? tokenizerConfigPath)
    {
        var templateStr = string.Empty;
        if (!string.IsNullOrWhiteSpace(tokenizerConfigPath) && File.Exists(tokenizerConfigPath))
        {
            try
            {
                using var configDoc = JsonDocument.Parse(File.ReadAllText(tokenizerConfigPath));
                if (configDoc.RootElement.TryGetProperty("chat_template", out var ct) && ct.ValueKind == JsonValueKind.String)
                    templateStr = ct.GetString() ?? string.Empty;
            }
            catch (JsonException) { }
        }

        if (templateStr.Contains("<|start_header_id|>") || _addedTokens.ContainsKey("<|start_header_id|>"))
            return TemplateStyle.Llama3;
        if (templateStr.Contains("<|im_start|>") || _addedTokens.ContainsKey("<|im_start|>"))
            return TemplateStyle.ChatML;
        if (templateStr.Contains("<|user|>") || templateStr.Contains("<|system|>") || _addedTokens.ContainsKey("<|user|>"))
            return TemplateStyle.Phi;

        return TemplateStyle.ChatML;
    }

    private static string ExtractSplitPattern(JsonElement root)
    {
        if (root.TryGetProperty("pre_tokenizer", out var preTok))
        {
            if (preTok.TryGetProperty("pretokenizers", out var stages) && stages.ValueKind == JsonValueKind.Array)
            {
                foreach (var stage in stages.EnumerateArray())
                {
                    if (stage.TryGetProperty("type", out var stageType) && stageType.GetString() == "Split" &&
                        stage.TryGetProperty("pattern", out var pat) && pat.TryGetProperty("Regex", out var regexElem) &&
                        regexElem.GetString() is { Length: > 0 } pattern)
                    {
                        return pattern;
                    }
                }
            }
            else if (preTok.TryGetProperty("pattern", out var pat) && pat.TryGetProperty("Regex", out var regexElem) &&
                     regexElem.GetString() is { Length: > 0 } pattern)
            {
                return pattern;
            }
        }

        // Universal standard BPE pre-tokenizer split regex (Llama 3 / Qwen 2.5 / GPT-4)
        return @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";
    }

    private static void EnsureValidUnicode(string text)
    {
        for (var i = 0; i < text.Length; ++i)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[++i]))
                throw new RuntimeException("invalid_message", "The message contains invalid Unicode text.");
        }
    }

    private sealed record AddedTokenRule(bool LeftStrip, bool RightStrip);

    /// <summary>
    /// HF's regex operates on Unicode scalar values. .NET Regex uses UTF-16.
    /// Collapse supplementary runes to category-equivalent BMP placeholders for
    /// matching, then return offsets into the unchanged original input.
    /// </summary>
    private sealed class ScalarRegexPreTokenizer(string pattern) : PreTokenizer
    {
        private readonly Regex _pattern = new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

        public override IEnumerable<(int Offset, int Length)> PreTokenize(ReadOnlySpan<char> text) => PreTokenize(text.ToString());

        public override IEnumerable<(int Offset, int Length)> PreTokenize(string text)
        {
            var proxy = new StringBuilder(text.Length);
            var offsets = new List<int>(text.Length + 1);
            var offset = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                offsets.Add(offset);
                proxy.Append(rune.IsBmp ? (char)rune.Value : Proxy(Rune.GetUnicodeCategory(rune)));
                offset += rune.Utf16SequenceLength;
            }
            offsets.Add(offset);
            foreach (Match match in _pattern.Matches(proxy.ToString()))
                yield return (offsets[match.Index], offsets[match.Index + match.Length] - offsets[match.Index]);
        }

        private static char Proxy(UnicodeCategory category) => category switch
        {
            UnicodeCategory.UppercaseLetter => 'Ā', UnicodeCategory.LowercaseLetter => 'ā',
            UnicodeCategory.TitlecaseLetter => 'ǅ', UnicodeCategory.ModifierLetter => 'ʰ',
            UnicodeCategory.OtherLetter => '中', UnicodeCategory.NonSpacingMark => '\u0301',
            UnicodeCategory.SpacingCombiningMark => '\u093e', UnicodeCategory.EnclosingMark => '\u20dd',
            UnicodeCategory.DecimalDigitNumber => '\u0661', UnicodeCategory.LetterNumber => '\u216b',
            UnicodeCategory.OtherNumber => '\u00b2', _ => '\ue000'
        };
    }
}
