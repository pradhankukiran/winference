using System.Text.Json;

namespace Winference.Core.Tools;

/// <summary>
/// A parameter descriptor for a local tool function.
/// </summary>
public sealed record ToolParameter(
    string Name,
    string Type,
    string Description,
    bool IsRequired = false);

/// <summary>
/// A callable local tool conforming to the Model Context Protocol (MCP) and OpenAI function schema.
/// </summary>
public sealed class LocalTool
{
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<ToolParameter> Parameters { get; }
    /// <summary>True when the tool can see the user's files, so the app asks before each use.</summary>
    public bool AsksFirst { get; }
    private readonly Func<JsonElement, CancellationToken, Task<string>> _handler;

    public LocalTool(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters,
        Func<JsonElement, CancellationToken, Task<string>> Handler,
        bool AsksFirst = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Description);
        this.Name = Name;
        this.Description = Description;
        this.Parameters = Parameters ?? [];
        this.AsksFirst = AsksFirst;
        _handler = Handler ?? throw new ArgumentNullException(nameof(Handler));
    }

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        _handler(arguments, cancellationToken);

    /// <summary>
    /// Formats the tool schema into standard JSON description for prompt injection.
    /// </summary>
    public string ToSchemaJson()
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in Parameters)
        {
            properties[p.Name] = new { type = p.Type, description = p.Description };
            if (p.IsRequired) required.Add(p.Name);
        }

        return JsonSerializer.Serialize(new
        {
            type = "function",
            function = new
            {
                name = Name,
                description = Description,
                parameters = new
                {
                    type = "object",
                    properties,
                    required
                }
            }
        });
    }
}
