namespace Winference.Core.Templates;

/// <summary>One piece of a rendered prompt. User text is marked so its special-looking markers are read as plain text.</summary>
public sealed record PromptPart(string Text, bool IsInput);

public sealed record ChatTemplateMessage(string Role, string Content);

/// <summary>
/// A model's own chat template (Jinja, as stored in its GGUF file), rendered the way Hugging Face
/// and llama.cpp render it: trim_blocks and lstrip_blocks, bos/eos tokens, and the generation prompt.
/// </summary>
public sealed class ChatTemplate
{
    private const string Marker = "⁣WINFERENCE_PROBE⁣";
    private readonly List<Node> _program;
    private readonly string _bos;
    private readonly string _eos;

    private ChatTemplate(List<Node> program, string bos, string eos)
    {
        _program = program;
        _bos = bos;
        _eos = eos;
        SupportsSystemRole = Probe([new("system", Marker), new("user", "Hello")], plainContent: true);
        SupportsStringContent = Probe([new("user", Marker)], plainContent: true);
        RequiresTypedContent = !SupportsStringContent && Probe([new("user", Marker)], plainContent: false);
    }

    /// <summary>False when the template rejects or ignores a system message; it is then merged into the first user turn.</summary>
    public bool SupportsSystemRole { get; }
    public bool SupportsStringContent { get; }
    /// <summary>True when the template only reads content as a list of {"type": "text"} parts.</summary>
    public bool RequiresTypedContent { get; }

    /// <summary>Clock for templates that print today's date.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <exception cref="TemplateException">The template cannot be read.</exception>
    public static ChatTemplate Parse(string source, string bosToken = "", string eosToken = "")
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(JinjaParser.Parse(source), bosToken ?? "", eosToken ?? "");
    }

    /// <summary>
    /// Renders the conversation. With <paramref name="removeLeadingBos"/>, a bos token the template writes
    /// first is dropped, because the tokenizer adds its own.
    /// </summary>
    /// <exception cref="TemplateException">The template refused the conversation or failed while rendering it.</exception>
    public IReadOnlyList<PromptPart> Render(IReadOnlyList<ChatTemplateMessage> messages, bool addGenerationPrompt = true,
        bool removeLeadingBos = false, IReadOnlyDictionary<string, object?>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var prepared = messages.ToList();
        if (!SupportsSystemRole && prepared.Count > 0 && prepared[0].Role == "system")
        {
            // As llama.cpp does: the system text joins the next message, or is dropped when alone.
            prepared = prepared.Count > 1
                ? [prepared[1] with { Content = prepared[0].Content + "\n" + prepared[1].Content }, .. prepared.Skip(2)]
                : [];
        }
        var output = Run(prepared, addGenerationPrompt, plainContent: !RequiresTypedContent, variables);
        var parts = Merge(output.Parts);
        if (removeLeadingBos && _bos.Length > 0 && parts.Count > 0 && !parts[0].IsInput && parts[0].Text.StartsWith(_bos, StringComparison.Ordinal))
        {
            var rest = parts[0].Text[_bos.Length..];
            parts = rest.Length > 0 ? [parts[0] with { Text = rest }, .. parts.Skip(1)] : [.. parts.Skip(1)];
        }
        return parts;
    }

    public static string Join(IReadOnlyList<PromptPart> parts) => string.Concat(parts.Select(part => part.Text));

    private JString Run(IReadOnlyList<ChatTemplateMessage> messages, bool addGenerationPrompt, bool plainContent,
        IReadOnlyDictionary<string, object?>? variables)
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (variables is not null)
            foreach (var (name, value) in variables) context[name] = Values.FromClr(value, markInput: false);
        context["messages"] = messages.Select(message => (object?)Values.ToMap([
            ("role", JString.Plain(message.Role)),
            ("content", plainContent ? JString.Input(message.Content)
                : new List<object?> { Values.ToMap([("type", JString.Plain("text")), ("text", JString.Input(message.Content))]) })
        ])).ToList();
        context["add_generation_prompt"] = addGenerationPrompt;
        context["bos_token"] = JString.Plain(_bos);
        context["eos_token"] = JString.Plain(_eos);
        return new JinjaRuntime(context) { Now = Now }.Render(_program);
    }

    private bool Probe(IReadOnlyList<ChatTemplateMessage> messages, bool plainContent)
    {
        try { return Run(messages, addGenerationPrompt: true, plainContent, null).Text.Contains(Marker, StringComparison.Ordinal); }
        catch (Exception) { return false; } // A probe only asks a question; any failure means "no".

    }

    private static List<PromptPart> Merge(IReadOnlyList<(string Text, bool Input)> parts)
    {
        var merged = new List<PromptPart>();
        foreach (var (text, input) in parts)
        {
            if (text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].IsInput == input) merged[^1] = merged[^1] with { Text = merged[^1].Text + text };
            else merged.Add(new(text, input));
        }
        return merged;
    }
}
