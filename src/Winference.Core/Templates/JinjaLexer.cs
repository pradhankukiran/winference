using System.Globalization;
using System.Text;

namespace Winference.Core.Templates;

/// <summary>A template that cannot be read or run. The message is safe to show; it never contains chat text.</summary>
public sealed class TemplateException(string message) : Exception(message);

internal enum TokenKind { Text, OutputOpen, OutputClose, BlockOpen, BlockClose, Name, String, Integer, Float, Operator, End }

internal readonly record struct Token(TokenKind Kind, string Value, int Position);

/// <summary>
/// Splits a Jinja template into text and tag tokens, the way chat templates are rendered by
/// Hugging Face: trim_blocks and lstrip_blocks on, plus the explicit "-" whitespace controls.
/// </summary>
internal static class JinjaLexer
{
    private static readonly string[] Operators =
        ["**", "//", "==", "!=", "<=", ">=", "+", "-", "*", "/", "%", "~", "<", ">", "=", "(", ")", "[", "]", "{", "}", ",", ".", ":", "|"];

    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var position = 0;
        var trimNextNewline = false;   // trim_blocks after a block or comment tag
        var stripNextWhitespace = false; // "-" at the end of the previous tag
        while (position < source.Length)
        {
            var open = FindTagStart(source, position);
            var text = source[position..(open < 0 ? source.Length : open)];
            if (stripNextWhitespace) text = text.TrimStart();
            else if (trimNextNewline) text = RemoveLeadingNewline(text);
            stripNextWhitespace = trimNextNewline = false;
            if (open < 0)
            {
                Add(tokens, text, position);
                break;
            }

            var kind = source[open + 1];
            var modifier = open + 2 < source.Length ? source[open + 2] : '\0';
            if (modifier == '-') text = text.TrimEnd();
            else if (kind is '%' or '#' && modifier != '+') text = StripLineIndent(text);
            Add(tokens, text, position);

            var contentStart = open + 2 + (modifier is '-' or '+' ? 1 : 0);
            if (kind == '#')
            {
                var close = source.IndexOf("#}", contentStart, StringComparison.Ordinal);
                if (close < 0) throw new TemplateException("A comment in the chat template is not closed.");
                stripNextWhitespace = source[close - 1] == '-' && close - 1 >= contentStart;
                trimNextNewline = !stripNextWhitespace;
                position = close + 2;
                continue;
            }

            if (kind == '%' && IsRawBlock(source, contentStart, out var rawBodyStart))
            {
                var (rawEnd, afterRaw) = FindEndRaw(source, rawBodyStart);
                Add(tokens, source[rawBodyStart..rawEnd], rawBodyStart);
                position = afterRaw;
                trimNextNewline = true;
                continue;
            }

            tokens.Add(new(kind == '{' ? TokenKind.OutputOpen : TokenKind.BlockOpen, "", open));
            position = ReadTag(source, contentStart, kind == '{' ? '}' : '%', tokens, out var stripAfter);
            tokens.Add(new(kind == '{' ? TokenKind.OutputClose : TokenKind.BlockClose, "", position));
            stripNextWhitespace = stripAfter;
            trimNextNewline = kind == '%' && !stripAfter;
        }
        tokens.Add(new(TokenKind.End, "", source.Length));
        return tokens;
    }

    private static void Add(List<Token> tokens, string text, int position)
    {
        if (text.Length > 0) tokens.Add(new(TokenKind.Text, text, position));
    }

    private static int FindTagStart(string source, int from)
    {
        for (var i = from; i < source.Length - 1; i++)
            if (source[i] == '{' && source[i + 1] is '{' or '%' or '#') return i;
        return -1;
    }

    private static string RemoveLeadingNewline(string text) =>
        text.StartsWith("\r\n", StringComparison.Ordinal) ? text[2..] : text.StartsWith('\n') ? text[1..] : text;

    /// <summary>lstrip_blocks: spaces and tabs between a line start and a block tag are dropped.</summary>
    private static string StripLineIndent(string text)
    {
        var end = text.Length;
        var i = end - 1;
        while (i >= 0 && text[i] is ' ' or '\t') i--;
        if (i < 0 || text[i] == '\n') return text[..(i + 1)];
        return text;
    }

    private static bool IsRawBlock(string source, int start, out int bodyStart)
    {
        var i = start;
        while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
        bodyStart = 0;
        if (string.CompareOrdinal(source, i, "raw", 0, 3) != 0) return false;
        i += 3;
        while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
        if (i < source.Length && source[i] == '-') i++;
        if (string.CompareOrdinal(source, i, "%}", 0, 2) != 0) return false;
        bodyStart = i + 2;
        return true;
    }

    private static (int BodyEnd, int After) FindEndRaw(string source, int from)
    {
        var search = from;
        while (true)
        {
            var open = source.IndexOf("{%", search, StringComparison.Ordinal);
            if (open < 0) throw new TemplateException("A raw block in the chat template is not closed.");
            var i = open + 2;
            if (i < source.Length && source[i] is '-' or '+') i++;
            while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
            if (string.CompareOrdinal(source, i, "endraw", 0, 6) == 0)
            {
                var close = source.IndexOf("%}", i, StringComparison.Ordinal);
                if (close < 0) throw new TemplateException("A raw block in the chat template is not closed.");
                return (open, close + 2);
            }
            search = open + 2;
        }
    }

    private static int ReadTag(string source, int i, char closer, List<Token> tokens, out bool stripAfter)
    {
        // "}}" inside a dict literal such as {'a': {'b': 1}} does not close the tag.
        var depth = 0;
        while (true)
        {
            while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
            if (i >= source.Length) throw new TemplateException("A tag in the chat template is not closed.");
            if (depth == 0 && source[i] == '-' && At(source, i + 1, closer))
            {
                stripAfter = true;
                return i + 3;
            }
            if (depth == 0 && At(source, i, closer))
            {
                stripAfter = false;
                return i + 2;
            }
            if (source[i] is '(' or '[' or '{') depth++;
            else if (source[i] is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            var c = source[i];
            if (c is '"' or '\'')
            {
                i = ReadString(source, i, tokens);
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                i = ReadNumber(source, i, tokens);
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                tokens.Add(new(TokenKind.Name, source[start..i], start));
                continue;
            }
            var op = Operators.FirstOrDefault(candidate => string.CompareOrdinal(source, i, candidate, 0, candidate.Length) == 0)
                ?? throw new TemplateException($"The chat template has an unexpected character at position {i}.");
            tokens.Add(new(TokenKind.Operator, op, i));
            i += op.Length;
        }
    }

    private static bool At(string source, int i, char closer) =>
        i + 1 < source.Length && source[i] == closer && source[i + 1] == '}';

    private static int ReadString(string source, int i, List<Token> tokens)
    {
        var quote = source[i];
        var start = i++;
        var value = new StringBuilder();
        while (true)
        {
            if (i >= source.Length) throw new TemplateException("A string in the chat template is not closed.");
            var c = source[i++];
            if (c == quote) break;
            if (c != '\\' || i >= source.Length) { value.Append(c); continue; }
            var escape = source[i++];
            switch (escape)
            {
                case 'n': value.Append('\n'); break;
                case 't': value.Append('\t'); break;
                case 'r': value.Append('\r'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'v': value.Append('\v'); break;
                case '0': value.Append('\0'); break;
                case '\\': value.Append('\\'); break;
                case '\'': value.Append('\''); break;
                case '"': value.Append('"'); break;
                case '\n': break;
                case 'x' when i + 2 <= source.Length && int.TryParse(source.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex):
                    value.Append((char)hex); i += 2; break;
                case 'u' when i + 4 <= source.Length && int.TryParse(source.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unit):
                    value.Append((char)unit); i += 4; break;
                case 'U' when i + 8 <= source.Length && int.TryParse(source.AsSpan(i, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var point):
                    value.Append(char.ConvertFromUtf32(point)); i += 8; break;
                default: value.Append('\\').Append(escape); break;
            }
        }
        tokens.Add(new(TokenKind.String, value.ToString(), start));
        return i;
    }

    private static int ReadNumber(string source, int i, List<Token> tokens)
    {
        var start = i;
        while (i < source.Length && (char.IsAsciiDigit(source[i]) || source[i] == '_')) i++;
        var isFloat = false;
        if (i + 1 < source.Length && source[i] == '.' && char.IsAsciiDigit(source[i + 1]))
        {
            isFloat = true;
            i++;
            while (i < source.Length && char.IsAsciiDigit(source[i])) i++;
        }
        if (i < source.Length && source[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < source.Length && source[j] is '+' or '-') j++;
            if (j < source.Length && char.IsAsciiDigit(source[j]))
            {
                isFloat = true;
                i = j;
                while (i < source.Length && char.IsAsciiDigit(source[i])) i++;
            }
        }
        tokens.Add(new(isFloat ? TokenKind.Float : TokenKind.Integer, source[start..i].Replace("_", ""), start));
        return i;
    }
}
