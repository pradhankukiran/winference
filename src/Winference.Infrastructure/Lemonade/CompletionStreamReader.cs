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
    }
}
