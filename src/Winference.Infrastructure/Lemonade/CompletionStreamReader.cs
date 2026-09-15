using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Lemonade;

/// <summary>Reads bounded OpenAI-compatible server-sent events. Network reads are
/// transport chunks, never token or event boundaries.</summary>
public static class CompletionStreamReader
{
    private const int MaximumLineBytes = 64 * 1024;
    private const int MaximumEventBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async IAsyncEnumerable<GenerationEvent> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[8192];
        var line = new byte[MaximumLineBytes];
        var lineLength = 0;
        var eventBytes = 0;
        var data = new StringBuilder();
        var eventType = string.Empty;
        var hasData = false;
        var firstLine = true;
        var skipLineFeed = false;
        var carriageReturnEndedEvent = false;
        string? finishReason = null;

        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = buffer[i];
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (value == '\n')
                    {
                        if (!carriageReturnEndedEvent && ++eventBytes > MaximumEventBytes)
                            throw ProtocolError("stream_limit", "The AI reply contained an oversized event.");
                        continue;
                    }
                }
                if (++eventBytes > MaximumEventBytes)
                    throw ProtocolError("stream_limit", "The AI reply contained an oversized event.");
                if (value is not (byte)'\r' and not (byte)'\n')
                {
                    if (lineLength == MaximumLineBytes)
                        throw ProtocolError("stream_limit", "The AI reply contained an oversized event line.");
                    line[lineLength++] = value;
                    continue;
                }

                skipLineFeed = value == '\r';
                carriageReturnEndedEvent = false;
                var text = DecodeLine(line, lineLength);
                if (firstLine)
                {
                    firstLine = false;
                    if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
                }
                if (text.Length == 0)
                {
                    if (eventType == "error")
                        throw ProtocolError("stream_error", "The AI server interrupted the reply. Try again.");
                    if (hasData)
                    {
                        // SSE adds a newline for each data field and removes the last one.
                        var payload = data.ToString(0, data.Length - 1);
                        if (payload.Trim() == "[DONE]")
                        {
                            yield return new GenerationEvent(GenerationEventKind.Completed, FinishReason: finishReason);
                            yield break;
                        }
                        foreach (var item in ParsePayload(payload, ref finishReason)) yield return item;
                    }
                    data.Clear(); eventType = string.Empty; hasData = false; eventBytes = 0;
                    carriageReturnEndedEvent = skipLineFeed;
                }
                else
                {
                    if (text[0] != ':')
                    {
                        var colon = text.IndexOf(':');
                        var field = colon < 0 ? text : text[..colon];
                        var fieldValue = colon < 0 ? string.Empty : text[(colon + 1)..];
                        if (fieldValue.StartsWith(' ')) fieldValue = fieldValue[1..];
                        if (field == "data") { data.Append(fieldValue).Append('\n'); hasData = true; }
                        else if (field == "event") eventType = fieldValue;
                        // id/retry and extension fields do not change completion content.
                    }
                }
                lineLength = 0;
            }
        }

        // A completed finish_reason is followed by optional usage and [DONE]. EOF
        // alone cannot prove successful completion, even after visible partial text.
        throw ProtocolError("stream_truncated", "The AI reply ended unexpectedly. The partial reply was kept; connect again and retry.");
    }

    private static string DecodeLine(byte[] bytes, int length)
    {
        try { return StrictUtf8.GetString(bytes, 0, length); }
        catch (DecoderFallbackException)
        {
            throw ProtocolError("stream_encoding", "The AI server returned invalid text encoding.");
        }
    }

    private static IReadOnlyList<GenerationEvent> ParsePayload(string payload, ref string? finishReason)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidPayload();
            if (root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
                throw ProtocolError("stream_error", "The AI server interrupted the reply. Try again.");
            var events = new List<GenerationEvent>(2);
            var hasChoices = root.TryGetProperty("choices", out var choices);
            var hasUsage = root.TryGetProperty("usage", out var usage) && usage.ValueKind is not JsonValueKind.Null;
            if (!hasChoices && !hasUsage) throw InvalidPayload();
            if (hasChoices)
            {
                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 1) throw InvalidPayload();
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.Object) throw InvalidPayload();
                    if (choice.TryGetProperty("index", out var index) && (!index.TryGetInt32(out var n) || n != 0)) throw InvalidPayload();
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.ValueKind != JsonValueKind.Object) throw InvalidPayload();
                        if (delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null &&
                            (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() > 0)) throw UnsupportedReply();
                        if (delta.TryGetProperty("function_call", out var function) && function.ValueKind != JsonValueKind.Null)
                            throw UnsupportedReply();
                        if (delta.TryGetProperty("content", out var content) && content.ValueKind is not JsonValueKind.Null)
                        {
                            if (content.ValueKind != JsonValueKind.String) throw InvalidPayload();
                            var text = content.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                if (finishReason is not null) throw InvalidPayload();
                                events.Add(new(GenerationEventKind.TextDelta, Text: text));
                            }
                        }
                    }
                    if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
                    {
                        if (finish.ValueKind != JsonValueKind.String) throw InvalidPayload();
                        var reason = finish.GetString();
                        if (finishReason is not null && finishReason != reason) throw InvalidPayload();
                        if (reason is not ("stop" or "length")) throw UnsupportedReply();
                        finishReason = reason;
                    }
                }
            }
            if (hasUsage)
            {
                if (usage.ValueKind != JsonValueKind.Object) throw InvalidPayload();
                var prompt = TokenCount(usage, "prompt_tokens");
                var completion = TokenCount(usage, "completion_tokens");
                events.Add(new(GenerationEventKind.Usage, Usage: new GenerationUsage(prompt, completion)));
            }
            return events;
        }
        catch (JsonException) { throw InvalidPayload(); }
        catch (InvalidOperationException) { throw InvalidPayload(); }
        catch (FormatException) { throw InvalidPayload(); }
    }

    private static int? TokenCount(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var token) || token.ValueKind == JsonValueKind.Null) return null;
        if (!token.TryGetInt32(out var count) || count < 0) throw InvalidPayload();
        return count;
    }

    private static RuntimeException InvalidPayload() =>
        ProtocolError("stream_format", "The AI server returned an invalid reply event. Try again.");

    private static RuntimeException UnsupportedReply() =>
        ProtocolError("stream_unsupported", "The AI server returned an unsupported reply. This chat supports text replies only.");

    private static RuntimeException ProtocolError(string code, string message) => new(code, message);
}
