using System.Globalization;
using System.Text;

namespace Winference.Core.Templates;

/// <summary>
/// A template string that remembers which parts came from the user. Text the user wrote is
/// later read without special-token parsing, so a typed marker cannot pose as a real turn.
/// </summary>
internal sealed class JString : IEquatable<JString>
{
    public static readonly JString Empty = new([]);
    private readonly (string Text, bool Input)[] _parts;
    private string? _text;

    private JString((string, bool)[] parts) => _parts = parts;

    public static JString Plain(string text) => text.Length == 0 ? Empty : new([(text, false)]);
    public static JString Input(string text) => text.Length == 0 ? Empty : new([(text, true)]);

    public IReadOnlyList<(string Text, bool Input)> Parts => _parts;
    public string Text => _text ??= string.Concat(_parts.Select(part => part.Text));
    public int Length => Text.Length;
    public bool AllInput => _parts.Length > 0 && _parts.All(part => part.Input);
    public bool AnyInput => _parts.Any(part => part.Input);

    public static JString Concat(params JString[] values)
    {
        var parts = new List<(string, bool)>();
        foreach (var value in values) parts.AddRange(value._parts);
        return parts.Count == 0 ? Empty : new([.. parts]);
    }

    /// <summary>A result derived from these strings: user text only if every source was entirely user text.</summary>
    public static JString Derived(string text, params JString[] sources) =>
        sources.Length > 0 && sources.All(source => source.AllInput || source.Length == 0) && sources.Any(source => source.Length > 0)
            ? Input(text) : Plain(text);

    /// <summary>Applies a character-for-character change, such as upper case, to every part.</summary>
    public JString Map(Func<string, string> change) => new([.. _parts.Select(part => (change(part.Text), part.Input))]);

    public JString TrimStart(char[]? characters = null)
    {
        var parts = _parts.ToList();
        while (parts.Count > 0)
        {
            var trimmed = characters is null ? parts[0].Item1.TrimStart() : parts[0].Item1.TrimStart(characters);
            if (trimmed.Length > 0) { parts[0] = (trimmed, parts[0].Item2); break; }
            parts.RemoveAt(0);
        }
        return parts.Count == 0 ? Empty : new([.. parts]);
    }

    public JString TrimEnd(char[]? characters = null)
    {
        var parts = _parts.ToList();
        while (parts.Count > 0)
        {
            var last = parts.Count - 1;
            var trimmed = characters is null ? parts[last].Item1.TrimEnd() : parts[last].Item1.TrimEnd(characters);
            if (trimmed.Length > 0) { parts[last] = (trimmed, parts[last].Item2); break; }
            parts.RemoveAt(last);
        }
        return parts.Count == 0 ? Empty : new([.. parts]);
    }

    public JString Trim(char[]? characters = null) => TrimStart(characters).TrimEnd(characters);

    public bool Equals(JString? other) => other is not null && Text == other.Text;
    public override bool Equals(object? obj) => obj is JString other && Equals(other);
    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => Text;
}

/// <summary>A missing variable or key. It renders as nothing and counts as false.</summary>
internal sealed class Undefined
{
    public static readonly Undefined Instance = new();
    private Undefined() { }
    public override string ToString() => string.Empty;
}

/// <summary>namespace(): the one object a template may change from inside a loop.</summary>
internal sealed class Namespace
{
    public OrderedDictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
}

internal sealed record Arguments(List<object?> Positional, Dictionary<string, object?> Keywords)
{
    public object? Get(int index, string name, object? fallback = null) =>
        Keywords.TryGetValue(name, out var keyword) ? keyword : index < Positional.Count ? Positional[index] : fallback;
}

internal sealed class Callable(string name, Func<Arguments, object?> invoke)
{
    public string Name { get; } = name;
    public object? Invoke(Arguments arguments) => invoke(arguments);
}

/// <summary>Python-like conversions, comparisons and printing used throughout the runtime.</summary>
internal static class Values
{
    public static bool Truthy(object? value) => value switch
    {
        null or Undefined => false,
        bool flag => flag,
        long number => number != 0,
        double number => number != 0,
        JString text => text.Length > 0,
        List<object?> list => list.Count > 0,
        OrderedDictionary<string, object?> map => map.Count > 0,
        _ => true
    };

    public static bool IsNumber(object? value) => value is long or double;
    public static double ToDouble(object? value) => value switch
    {
        long number => number,
        double number => number,
        bool flag => flag ? 1 : 0,
        _ => throw new TemplateException("The chat template used a value that is not a number.")
    };

    public static new bool Equals(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (Undefined, Undefined) => true,
        (bool a, bool b) => a == b,
        (bool, long or double) or (long or double, bool) => ToDouble(left) == ToDouble(right),
        (long a, long b) => a == b,
        (long or double, long or double) => ToDouble(left) == ToDouble(right),
        (JString a, JString b) => a.Text == b.Text,
        (List<object?> a, List<object?> b) => a.Count == b.Count && a.Zip(b).All(pair => Equals(pair.First, pair.Second)),
        (OrderedDictionary<string, object?> a, OrderedDictionary<string, object?> b) =>
            a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && Equals(pair.Value, other)),
        _ => ReferenceEquals(left, right)
    };

    public static int Compare(object? left, object? right) => (left, right) switch
    {
        (long or double or bool, long or double or bool) => ToDouble(left).CompareTo(ToDouble(right)),
        (JString a, JString b) => string.CompareOrdinal(a.Text, b.Text),
        (List<object?> a, List<object?> b) => CompareLists(a, b),
        _ => throw new TemplateException("The chat template compared values that cannot be ordered.")
    };

    private static int CompareLists(List<object?> a, List<object?> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var result = Compare(a[i], b[i]);
            if (result != 0) return result;
        }
        return a.Count.CompareTo(b.Count);
    }

    /// <summary>What {{ value }} prints, as Jinja does with Python values.</summary>
    public static JString ToJString(object? value) => value switch
    {
        JString text => text,
        Undefined => JString.Empty,
        _ => JString.Plain(Repr(value, top: true))
    };

    public static string Repr(object? value, bool top = false) => value switch
    {
        null => "None",
        Undefined => "",
        bool flag => flag ? "True" : "False",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => FormatDouble(number),
        JString text => top ? text.Text : QuoteString(text.Text),
        List<object?> list => "[" + string.Join(", ", list.Select(item => Repr(item))) + "]",
        OrderedDictionary<string, object?> map => "{" + string.Join(", ", map.Select(pair => QuoteString(pair.Key) + ": " + Repr(pair.Value))) + "}",
        Namespace => "<Namespace>",
        Callable callable => $"<function {callable.Name}>",
        _ => value.ToString() ?? ""
    };

    public static string FormatDouble(double number)
    {
        if (double.IsNaN(number)) return "nan";
        if (double.IsPositiveInfinity(number)) return "inf";
        if (double.IsNegativeInfinity(number)) return "-inf";
        if (number == Math.Floor(number) && Math.Abs(number) < 1e16) return number.ToString("0.0", CultureInfo.InvariantCulture);
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E') ? text.Replace("E", "e") : text;
    }

    private static string QuoteString(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c == quote) builder.Append('\\');
                    builder.Append(c);
                    break;
            }
        }
        return builder.Append(quote).ToString();
    }

    /// <summary>Items of a value a loop or filter can walk through.</summary>
    public static List<object?> Iterate(object? value) => value switch
    {
        null or Undefined => [],
        List<object?> list => list,
        OrderedDictionary<string, object?> map => [.. map.Keys.Select(key => (object?)JString.Plain(key))],
        JString text => [.. EnumerateCharacters(text)],
        Namespace space => [.. space.Values.Keys.Select(key => (object?)JString.Plain(key))],
        _ => throw new TemplateException("The chat template tried to loop over a value that is not a list.")
    };

    private static IEnumerable<object?> EnumerateCharacters(JString text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text.Text);
        while (enumerator.MoveNext()) yield return JString.Derived((string)enumerator.Current, text);
    }

    public static string Key(object? value) => value switch
    {
        JString text => text.Text,
        null => "None",
        _ => Repr(value)
    };

    /// <summary>Converts plain .NET data (strings, numbers, lists, dictionaries) into template values.</summary>
    public static object? FromClr(object? value, bool markInput) => value switch
    {
        null => null,
        string text => markInput ? JString.Input(text) : JString.Plain(text),
        bool or long or double => value,
        int number => (long)number,
        float number => (double)number,
        decimal number => (double)number,
        System.Text.Json.JsonElement element => FromJson(element, markInput),
        IDictionary<string, object?> map => ToMap(map.Select(pair => (pair.Key, FromClr(pair.Value, markInput)))),
        System.Collections.IEnumerable items => items.Cast<object?>().Select(item => FromClr(item, markInput)).ToList(),
        _ => throw new TemplateException("The chat template received an unsupported value.")
    };

    public static object? FromJson(System.Text.Json.JsonElement element, bool markInput) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble(),
        System.Text.Json.JsonValueKind.String => markInput ? JString.Input(element.GetString()!) : JString.Plain(element.GetString()!),
        System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(item => FromJson(item, markInput)).ToList(),
        _ => ToMap(element.EnumerateObject().Select(property => (property.Name, FromJson(property.Value, markInput))))
    };

    public static OrderedDictionary<string, object?> ToMap(IEnumerable<(string Key, object? Value)> pairs)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs) map[key] = value;
        return map;
    }
}
