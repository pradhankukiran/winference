using System.Text.RegularExpressions;

namespace Winference.Core.Voice;

/// <summary>Turns a Markdown reply into text that sounds natural when read aloud.</summary>
public static partial class SpeechText
{
    public const int MaxChars = 20000;

    public static string FromMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var text = CodeBlock().Replace(markdown, " (code block) ");
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = InlineCode().Replace(text, "$1");
        text = Heading().Replace(text, string.Empty);
        text = HorizontalRule().Replace(text, string.Empty);
        text = ListMarker().Replace(text, string.Empty);
        text = Quote().Replace(text, string.Empty);
        text = TableRule().Replace(text, string.Empty);
        text = text.Replace("|", ", ");
        text = Emphasis().Replace(text, "$2");
        text = Spaces().Replace(text, " ");
        text = string.Join('\n', text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
        return text.Length <= MaxChars ? text : text[..MaxChars];
    }

    [GeneratedRegex(@"```[\s\S]*?(```|$)")] private static partial Regex CodeBlock();
    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")] private static partial Regex Image();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"`([^`]*)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s*")] private static partial Regex Heading();
    [GeneratedRegex(@"(?m)^\s*([-*+]|\d+[.)])\s+")] private static partial Regex ListMarker();
    [GeneratedRegex(@"(?m)^\s*>\s?")] private static partial Regex Quote();
    [GeneratedRegex(@"(?m)^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")] private static partial Regex TableRule();
    [GeneratedRegex(@"(\*\*|__|\*|_|~~)(\S(?:.*?\S)?)\1")] private static partial Regex Emphasis();
    [GeneratedRegex(@"(?m)^\s*([-*_]\s*){3,}$")] private static partial Regex HorizontalRule();
    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
}
