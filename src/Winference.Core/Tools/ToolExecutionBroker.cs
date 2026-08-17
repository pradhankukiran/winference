using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Winference.Core.Tools;

/// <summary>A tool request found in a model reply. It has not run yet.</summary>
public sealed record ToolCall(string Name, JsonElement Arguments, string ArgumentsJson);

/// <summary>
/// Describes local tools to a model and finds tool requests in its replies. Local models
/// have no native function-calling channel here, so requests use a tagged JSON convention.
/// Parsing is separate from running so the app can ask the user first.
/// </summary>
public sealed partial class ToolExecutionBroker
{
    public const int MaxResultChars = 6000;
    private readonly Dictionary<string, LocalTool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<LocalTool> RegisteredTools => _tools.Values;

    public void Register(LocalTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
    }

    public void RegisterRange(IEnumerable<LocalTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var tool in tools) Register(tool);
    }

    public LocalTool? Find(string name) => _tools.GetValueOrDefault(name);

    /// <summary>Instructions for the system message that describe the tools and how to call one.</summary>
    public string GenerateToolPrompt()
    {
        if (_tools.Count == 0) return string.Empty;
        var builder = new StringBuilder();
        builder.AppendLine("You can use these tools on the user's Windows PC:");
        foreach (var tool in _tools.Values)
        {
            builder.Append("- ").Append(tool.Name).Append(": ").AppendLine(tool.Description);
            foreach (var parameter in tool.Parameters)
                builder.Append("    ").Append(parameter.Name).Append(" (").Append(parameter.Type)
                    .Append(parameter.IsRequired ? ", required" : ", optional").Append("): ").AppendLine(parameter.Description);
        }
        builder.AppendLine();
        builder.AppendLine("Only use a tool when the question needs live information from this PC. To use one, reply with nothing but:");
        builder.AppendLine("<tool_call>{\"name\": \"tool_name\", \"arguments\": {\"argument\": \"value\"}}</tool_call>");
        builder.AppendLine("Then stop. The result will be sent to you, and you then answer the user normally.");
        builder.AppendLine("Never write a tool result yourself or guess what a tool would return.");
        return builder.ToString();
    }

    /// <summary>True when a reply seems to request a tool, whether or not the request can be read.</summary>
    public static bool LooksLikeToolCall(string text) =>
        !string.IsNullOrEmpty(text) && text.Contains("tool_call", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the first request for a registered tool, or null if the reply has none.</summary>
    public ToolCall? ParseToolCall(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _tools.Count == 0) return null;
        foreach (var candidate in Candidates(text))
        foreach (var json in JsonObjects(candidate))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                // OpenAI style nests the call: {"function": {"name": ..., "arguments": ...}}
                if (root.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object) root = function;
                string? name = null;
                foreach (var key in NameKeys)
                    if (root.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String) { name = n.GetString(); break; }
                if (name is null || !_tools.ContainsKey(name)) continue;
                var arguments = root.TryGetProperty("arguments", out var a) || root.TryGetProperty("parameters", out a)
                    ? a.ValueKind == JsonValueKind.String ? ParseNested(a.GetString()) : a.Clone()
                    : EmptyObject();
                if (arguments.ValueKind != JsonValueKind.Object) arguments = EmptyObject();
                return new(_tools[name].Name, arguments, arguments.GetRawText());
            }
            catch (JsonException) { }
        }
        return ParseNameFirst(text);
    }

    /// <summary>
    /// Small models often put the name outside the JSON: <c>&lt;tool_call&gt;"list_directory", {...}</c>
    /// or <c>list_directory({...})</c>. Only registered names count.
    /// </summary>
    private ToolCall? ParseNameFirst(string text)
    {
        var starts = new List<(string Name, int After)>();
        foreach (Match match in AfterMarker().Matches(text)) starts.Add((match.Groups["name"].Value, match.Index + match.Length));
        foreach (Match match in CallSyntax().Matches(text)) starts.Add((match.Groups["name"].Value, match.Index + match.Length));
        foreach (var (name, after) in starts)
        {
            if (Find(name) is not { } tool) continue;
            var rest = text[after..];
            var trimmed = rest.TrimStart();
            // The arguments must follow the name directly, not appear somewhere later in the reply.
            var json = trimmed.StartsWith('{') ? JsonObjects(trimmed).FirstOrDefault() : null;
            if (json is null && tool.Parameters.Any(parameter => parameter.IsRequired)) continue;
            var arguments = json is null ? EmptyObject() : ParseNested(json);
            if (arguments.ValueKind != JsonValueKind.Object) continue;
            return new(tool.Name, arguments, arguments.GetRawText());
        }
        return null;
    }

    /// <summary>Runs a parsed call. Failures become an error result the model can read; the output is bounded.</summary>
    public async Task<string> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_tools.TryGetValue(call.Name, out var tool))
            return JsonSerializer.Serialize(new { error = $"Tool '{call.Name}' is not available." });
        string result;
        try { result = await tool.ExecuteAsync(call.Arguments, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { result = JsonSerializer.Serialize(new { error = $"{call.Name} failed: {error.Message}" }); }
        return result.Length <= MaxResultChars ? result : result[..MaxResultChars] + " …[truncated]";
    }

    /// <summary>Tagged calls first, then fenced JSON, then a bare object that names a tool.</summary>
    private static IEnumerable<string> Candidates(string text)
    {
        foreach (Match match in TaggedCall().Matches(text)) yield return match.Groups["body"].Value;
        foreach (Match match in FencedJson().Matches(text)) yield return match.Groups["body"].Value;
        yield return text;
    }
}
