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
}
