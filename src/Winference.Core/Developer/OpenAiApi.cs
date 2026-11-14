using System.Text;
using System.Text.Json;
using Winference.Core.Runtime;

namespace Winference.Core.Developer;

/// <summary>An answer to an app that the server sends as an OpenAI-style error.</summary>
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Where a reply to an app is written. The server owns the connection behind it.</summary>
public interface IApiResponse
{
    /// <summary>True once anything has been sent. After that, errors go inside the stream.</summary>
    bool HasStarted { get; }
    Task SendJsonAsync(int status, string json, CancellationToken cancellationToken);
    Task BeginEventStreamAsync(CancellationToken cancellationToken);
    Task SendEventAsync(string data, CancellationToken cancellationToken);
}

/// <param name="MaxTokens">Null lets the reply use the rest of the model's context.</param>
public sealed record ApiChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    int? MaxTokens,
    double? Temperature,
    bool Stream,
    bool IncludeUsage,
    string? Model);

/// <summary>Reads and writes the parts of the OpenAI chat API that Winference supports.</summary>
public static class OpenAiApi
{
    /// <summary>The engine accepts at most this many messages; older turns are dropped first.</summary>
    public const int MaxMessages = 64;

    public static ApiChatRequest ParseChatRequest(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body.ToArray()); }
        catch (JsonException) { throw BadRequest("invalid_json", "The request body isn't valid JSON."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw BadRequest("invalid_json", "The request body must be a JSON object.");
            if (!root.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                throw BadRequest("invalid_messages", "Send at least one message in \"messages\".");
            if (root.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number && (!n.TryGetInt32(out var count) || count != 1))
                throw BadRequest("unsupported_n", "Winference writes one reply per request. Leave \"n\" at 1.");

            var messages = new List<ChatMessage>();
            foreach (var item in list.EnumerateArray()) if (ParseMessage(item) is { } message) messages.Add(message);
            if (messages.Count == 0 || messages[^1].Role != "user")
                throw BadRequest("invalid_messages", "The last message must come from the user.");
            messages = Trim(messages);

            int? maxTokens = null;
            foreach (var name in new[] { "max_completion_tokens", "max_tokens" })
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var limit) || limit < 1)
                    throw BadRequest("invalid_max_tokens", $"\"{name}\" must be a whole number of at least 1.");
                maxTokens = limit;
                break;
            }

            double? temperature = null;
            if (root.TryGetProperty("temperature", out var temp) && temp.ValueKind != JsonValueKind.Null)
            {
                if (temp.ValueKind != JsonValueKind.Number || !double.IsFinite(temp.GetDouble()) || temp.GetDouble() is < 0 or > 2)
                    throw BadRequest("invalid_temperature", "\"temperature\" must be a number from 0 to 2.");
                temperature = temp.GetDouble();
            }

            var stream = root.TryGetProperty("stream", out var streamValue) && streamValue.ValueKind == JsonValueKind.True;
            var usage = root.TryGetProperty("stream_options", out var options) && options.ValueKind == JsonValueKind.Object &&
                options.TryGetProperty("include_usage", out var include) && include.ValueKind == JsonValueKind.True;
            var model = root.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String ? modelValue.GetString() : null;
            return new(messages, maxTokens, temperature, stream, usage, model);
        }
    }

    /// <summary>A short, single-line look at what was asked, for the request log on this PC.</summary>
    public static string Preview(ApiChatRequest request, int length = 90)
    {
        var text = request.Messages.LastOrDefault(message => message.Role == "user")?.Content ?? string.Empty;
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= length ? text : text[..(length - 1)] + "…";
    }

    public static string Error(int status, string code, string message)
    {
        var type = status switch { 400 or 404 or 405 or 413 => "invalid_request_error", 401 or 403 => "permission_error", _ => "server_error" };
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("message", message);
            writer.WriteString("type", type);
            writer.WriteString("code", code);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    public static string ModelList(string? modelId) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("object", "list");
        writer.WriteStartArray("data");
        if (modelId is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("id", modelId);
            writer.WriteString("object", "model");
            writer.WriteNumber("created", 0);
            writer.WriteString("owned_by", "winference");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string Completion(string id, long created, string model, string text, string? reasoning,
        string finishReason, int promptTokens, int completionTokens) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("object", "chat.completion");
        writer.WriteNumber("created", created);
        writer.WriteString("model", model);
        writer.WriteStartArray("choices");
        writer.WriteStartObject();
        writer.WriteNumber("index", 0);
        writer.WriteStartObject("message");
        writer.WriteString("role", "assistant");
        writer.WriteString("content", text);
        if (!string.IsNullOrEmpty(reasoning)) writer.WriteString("reasoning_content", reasoning);
        writer.WriteEndObject();
        writer.WriteString("finish_reason", finishReason);
        writer.WriteEndObject();
        writer.WriteEndArray();
        WriteUsage(writer, promptTokens, completionTokens);
        writer.WriteEndObject();
    });

    /// <param name="field">"role", "content" or "reasoning_content"; null sends an empty delta.</param>
    public static string Chunk(string id, long created, string model, string? field, string? value, string? finishReason) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("object", "chat.completion.chunk");
        writer.WriteNumber("created", created);
        writer.WriteString("model", model);
        writer.WriteStartArray("choices");
        writer.WriteStartObject();
        writer.WriteNumber("index", 0);
        writer.WriteStartObject("delta");
        if (field is not null) writer.WriteString(field, value);
        writer.WriteEndObject();
        if (finishReason is null) writer.WriteNull("finish_reason");
        else writer.WriteString("finish_reason", finishReason);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string UsageChunk(string id, long created, string model, int promptTokens, int completionTokens) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("object", "chat.completion.chunk");
        writer.WriteNumber("created", created);
        writer.WriteString("model", model);
        writer.WriteStartArray("choices");
        writer.WriteEndArray();
        WriteUsage(writer, promptTokens, completionTokens);
        writer.WriteEndObject();
    });

    private static void WriteUsage(Utf8JsonWriter writer, int promptTokens, int completionTokens)
    {
        writer.WriteStartObject("usage");
        writer.WriteNumber("prompt_tokens", promptTokens);
        writer.WriteNumber("completion_tokens", completionTokens);
        writer.WriteNumber("total_tokens", promptTokens + completionTokens);
        writer.WriteEndObject();
    }

    private static ChatMessage? ParseMessage(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("role", out var roleValue) || roleValue.ValueKind != JsonValueKind.String)
            throw BadRequest("invalid_messages", "Each message needs a \"role\".");
        var role = roleValue.GetString() switch
        {
            "system" or "developer" => "system",
            "user" => "user",
            "assistant" => "assistant",
            "tool" or "function" => throw BadRequest("unsupported_role", "Tool results aren't supported. Winference doesn't run function calls yet."),
            var other => throw BadRequest("invalid_messages", $"The role \"{other}\" isn't supported.")
        };
        var text = new StringBuilder();
        List<ChatImage>? images = null;
        if (item.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    var type = part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                    if (type == "text" && part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(partText.GetString());
                    }
                    else if (type == "image_url")
                    {
                        if (role != "user") throw BadRequest("invalid_image", "Only user messages can include pictures.");
                        (images ??= []).Add(ParseImage(part));
                    }
                    else throw BadRequest("unsupported_content", $"Message parts of type \"{type}\" aren't supported.");
                }
            }
            else if (content.ValueKind != JsonValueKind.Null)
                throw BadRequest("invalid_messages", "Message content must be text or a list of parts.");
        }
        var value = text.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            // Empty system or assistant turns (such as a tool call's placeholder) carry nothing the model can use.
            if (role != "user") return null;
            throw BadRequest("invalid_messages", "User messages need some text, even when they include a picture.");
        }
        return new(role, value, images);
    }

    private static ChatImage ParseImage(JsonElement part)
    {
        string? url = null;
        if (part.TryGetProperty("image_url", out var image))
        {
            if (image.ValueKind == JsonValueKind.String) url = image.GetString();
            else if (image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out var urlValue)) url = urlValue.GetString();
        }
        const string Marker = ";base64,";
        var split = url?.IndexOf(Marker, StringComparison.Ordinal) ?? -1;
        if (url is null || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || split < 0)
            throw BadRequest("invalid_image", "Send pictures as base64 data URLs. Web addresses aren't fetched.");
        var mediaType = url[5..split].ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpeg" or "image/jpg" => "image/jpeg",
            _ => throw BadRequest("invalid_image", "Pictures must be PNG or JPEG.")
        };
        try { return new(mediaType, Convert.FromBase64String(url[(split + Marker.Length)..])); }
        catch (FormatException) { throw BadRequest("invalid_image", "A picture's base64 data couldn't be read."); }
    }

    /// <summary>Keeps leading system messages and the newest turns.</summary>
    private static List<ChatMessage> Trim(List<ChatMessage> messages)
    {
        if (messages.Count <= MaxMessages) return messages;
        var system = messages.TakeWhile(message => message.Role == "system").ToList();
        if (system.Count >= MaxMessages) throw BadRequest("invalid_messages", "Too many system messages.");
        var recent = messages.Skip(system.Count).TakeLast(MaxMessages - system.Count).ToList();
        while (recent.Count > 1 && recent[0].Role != "user") recent.RemoveAt(0);
        return [.. system, .. recent];
    }

    private static ApiException BadRequest(string code, string message) => new(400, code, message);

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
