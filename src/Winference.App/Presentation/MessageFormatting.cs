using System.Text;

namespace Winference.App.Presentation;

public enum MessageBlockKind { Paragraph, Heading, ListItem, Quote, Code, Table }
public enum MessageInlineKind { Text, Strong, Code }
public sealed record MessageInline(MessageInlineKind Kind, string Text);
public sealed record MessageBlock(MessageBlockKind Kind, string Text, string Source,
    string Detail = "", int Level = 0, IReadOnlyList<IReadOnlyList<string>>? Rows = null);

/// <summary>
/// A deliberately small, presentation-only Markdown subset. Unsupported syntax
/// remains literal text. This parser never changes the stored message.
/// </summary>
public static class MessageFormatting
{
    public static IReadOnlyList<MessageBlock> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<MessageBlock>();
        var index = 0;
        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) { index++; continue; }
            var start = index;
            if (TryFence(lines[index], out var marker, out var length, out var language))
            {
                index++;
                var bodyStart = index;
                while (index < lines.Length && !IsClosingFence(lines[index], marker, length)) index++;
                var body = string.Join("\n", lines[bodyStart..index]);
                if (index < lines.Length)
                {
                    if (index > bodyStart) body += "\n";
                    index++;
                }
                blocks.Add(new(MessageBlockKind.Code, body, Source(lines, start, index), language));
            }
            else if (TryHeading(lines[index], out var heading, out var level))
            {
                blocks.Add(new(MessageBlockKind.Heading, heading, lines[index++], Level: level));
            }
            else if (TryListItem(lines[index], out var item, out var bullet, out var indent))
            {
                blocks.Add(new(MessageBlockKind.ListItem, item, lines[index++], bullet, indent));
            }
            else if (TryQuote(lines[index], out _))
            {
                var quote = new List<string>();
                while (index < lines.Length && TryQuote(lines[index], out var line))
                { quote.Add(line); index++; }
                blocks.Add(new(MessageBlockKind.Quote, string.Join("\n", quote), Source(lines, start, index)));
            }
            else if (TryTableHeader(lines, index, out var header))
            {
                var rows = new List<IReadOnlyList<string>> { header };
                index += 2;
                while (index < lines.Length && TryTableRow(lines[index], out var cells) && cells.Count == header.Count)
                { rows.Add(cells); index++; }
                blocks.Add(new(MessageBlockKind.Table, string.Empty, Source(lines, start, index), Rows: rows));
            }
            else
            {
                index++;
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !StartsBlock(lines, index)) index++;
                var paragraph = Source(lines, start, index);
                blocks.Add(new(MessageBlockKind.Paragraph, paragraph, paragraph));
            }
        }
        return blocks;
    }

    public static IReadOnlyList<MessageInline> ParseInline(string text)
    {
        var result = new List<MessageInline>();
        var literal = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            var marker = text[i] == '`' && (i + 1 == text.Length || text[i + 1] != '`') ? "`"
                : i + 1 < text.Length && (text.AsSpan(i).StartsWith("**") || text.AsSpan(i).StartsWith("__")) ? text.Substring(i, 2) : null;
            var end = marker is null ? -1 : text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
            if (marker is not null && end > i + marker.Length &&
                (marker != "`" || end + 1 == text.Length || text[end + 1] != '`'))
            {
                if (literal.Length > 0) { result.Add(new(MessageInlineKind.Text, literal.ToString())); literal.Clear(); }
                result.Add(new(marker == "`" ? MessageInlineKind.Code : MessageInlineKind.Strong,
                    text.Substring(i + marker.Length, end - i - marker.Length)));
                i = end + marker.Length;
            }
            else { literal.Append(text[i]); i++; }
        }
        if (literal.Length > 0) result.Add(new(MessageInlineKind.Text, literal.ToString()));
        return result;
    }

    private static string Source(string[] lines, int start, int end) => string.Join("\n", lines[start..end]);
    private static bool StartsBlock(string[] lines, int index) =>
        TryFence(lines[index], out _, out _, out _) || TryHeading(lines[index], out _, out _) ||
        TryListItem(lines[index], out _, out _, out _) || TryQuote(lines[index], out _) || TryTableHeader(lines, index, out _);

    private static bool TryFence(string line, out char marker, out int length, out string language)
    {
        var trimmed = line.TrimStart();
        marker = trimmed.Length == 0 ? '\0' : trimmed[0];
        length = 0;
        language = string.Empty;
        if (marker is not ('`' or '~')) return false;
        while (length < trimmed.Length && trimmed[length] == marker) length++;
        if (length < 3) return false;
        language = trimmed[length..].Trim();
        return marker != '`' || !language.Contains('`');
    }

    private static bool IsClosingFence(string line, char marker, int length)
    {
        var trimmed = line.Trim();
        var count = 0;
        while (count < trimmed.Length && trimmed[count] == marker) count++;
        return count >= length && count == trimmed.Length;
    }

    private static bool TryHeading(string line, out string text, out int level)
    {
        var trimmed = line.TrimStart();
        level = 0;
        while (level < trimmed.Length && trimmed[level] == '#') level++;
        text = string.Empty;
        if (level is < 1 or > 6 || level < trimmed.Length && !char.IsWhiteSpace(trimmed[level])) return false;
        text = trimmed[level..].TrimStart();
        return true;
    }

    private static bool TryListItem(string line, out string text, out string marker, out int indent)
    {
        var trimmed = line.TrimStart();
        indent = Math.Min(6, (line.Length - trimmed.Length) / 2);
        text = marker = string.Empty;
        if (trimmed.Length < 2) return false;
        var length = 0;
        if (trimmed[0] is '-' or '*' or '+') length = 1;
        else
        {
            while (length < trimmed.Length && char.IsAsciiDigit(trimmed[length])) length++;
            if (length is < 1 or > 9 || length >= trimmed.Length || trimmed[length] is not ('.' or ')')) return false;
            length++;
        }
        if (length >= trimmed.Length || !char.IsWhiteSpace(trimmed[length])) return false;
        marker = trimmed[0] is '-' or '*' or '+' ? "•" : trimmed[..length];
        text = trimmed[(length + 1)..];
        return true;
    }
}
