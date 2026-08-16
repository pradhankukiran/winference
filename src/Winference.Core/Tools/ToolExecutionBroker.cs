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
}
