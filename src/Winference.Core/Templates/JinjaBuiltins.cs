using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Winference.Core.Templates;

/// <summary>The filters, tests and Python-style methods that chat templates use.</summary>
internal sealed partial class JinjaRuntime
{
    private object? ApplyFilter(string name, object? value, Arguments arguments)
    {
        switch (name)
        {
            case "safe" or "escape" or "e" or "forceescape": return value;
            case "string": return Values.ToJString(value);
            case "length" or "count":
                return value switch
                {
                    JString text => (long)new StringInfo(text.Text).LengthInTextElements,
                    List<object?> list => (long)list.Count,
                    OrderedDictionary<string, object?> map => (long)map.Count,
                    Namespace space => (long)space.Values.Count,
                    null or Undefined => 0L,
                    _ => throw new TemplateException("The chat template took the length of a value that has none.")
                };
            case "trim":
                return Text(value).Trim(Characters(arguments.Get(0, "chars")));
            case "lower": return Text(value).Map(text => text.ToLowerInvariant());
            case "upper": return Text(value).Map(text => text.ToUpperInvariant());
            case "title": return Text(value).Map(Title);
            case "capitalize": return Text(value).Map(Capitalize);
            case "default" or "d":
            {
                var fallback = arguments.Get(0, "default_value", JString.Empty);
                var boolean = Values.Truthy(arguments.Get(1, "boolean", false));
                return value is Undefined || (boolean && !Values.Truthy(value)) ? fallback : value;
            }
            case "items":
                return value switch
                {
                    OrderedDictionary<string, object?> map => map.Select(pair => (object?)new List<object?> { JString.Plain(pair.Key), pair.Value }).ToList(),
                    Namespace space => space.Values.Select(pair => (object?)new List<object?> { JString.Plain(pair.Key), pair.Value }).ToList(),
                    null or Undefined => new List<object?>(),
                    _ => throw new TemplateException("The chat template listed the items of a value that is not a mapping.")
                };
            case "dictsort":
            {
                if (value is not OrderedDictionary<string, object?> map)
                    throw new TemplateException("The chat template sorted a value that is not a mapping.");
                var byValue = Values.Key(arguments.Get(1, "by", JString.Plain("key"))) == "value";
                var pairs = map.Select(pair => (object?)new List<object?> { JString.Plain(pair.Key), pair.Value }).ToList();
                pairs.Sort((x, y) => Values.Compare(((List<object?>)x!)[byValue ? 1 : 0], ((List<object?>)y!)[byValue ? 1 : 0]));
                if (Values.Truthy(arguments.Get(2, "reverse", false))) pairs.Reverse();
                return pairs;
            }
            case "list": return Values.Iterate(value).ToList();
            case "first": return Values.Iterate(value) is { Count: > 0 } firstItems ? firstItems[0] : Undefined.Instance;
            case "last": return Values.Iterate(value) is { Count: > 0 } lastItems ? lastItems[^1] : Undefined.Instance;
            case "reverse":
                return value is JString reversible ? JString.Derived(new string(reversible.Text.Reverse().ToArray()), reversible)
                    : Enumerable.Reverse(Values.Iterate(value)).ToList();
            case "join":
            {
                var separator = Text(arguments.Get(0, "d", JString.Empty));
                var attribute = arguments.Get(1, "attribute");
                var items = Values.Iterate(value).Select(item => Values.ToJString(attribute is null ? item : GetAttributePath(item, attribute))).ToList();
                var parts = new List<JString>();
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) parts.Add(separator);
                    parts.Add(items[i]);
                }
                return JString.Concat([.. parts]);
            }
            case "replace":
            {
                var text = Text(value);
                var old = Text(arguments.Get(0, "old")).Text;
                var replacement = Text(arguments.Get(1, "new"));
                var count = arguments.Get(2, "count") is long limit ? limit : -1;
                return JString.Derived(Replace(text.Text, old, replacement.Text, count), text, replacement);
            }
            case "indent":
            {
                var text = Text(value);
                var width = arguments.Get(0, "width", 4L) is long spaces ? new string(' ', (int)spaces) : Text(arguments.Get(0, "width")).Text;
                var first = Values.Truthy(arguments.Get(1, "first", false));
                var blank = Values.Truthy(arguments.Get(2, "blank", false));
                var lines = text.Text.Split('\n');
                var builder = new StringBuilder();
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) builder.Append('\n');
                    if ((i > 0 || first) && (blank || lines[i].Length > 0)) builder.Append(width);
                    builder.Append(lines[i]);
                }
                return JString.Derived(builder.ToString(), text);
            }
            case "tojson":
            {
                var indent = arguments.Get(0, "indent") is long size ? (int)size : (int?)null;
                var sortKeys = Values.Truthy(arguments.Get(1, "sort_keys", false));
                var separators = arguments.Keywords.GetValueOrDefault("separators") as List<object?>;
                var json = Json(value, indent, sortKeys, separators is { Count: 2 } ? (Text(separators[0]).Text, Text(separators[1]).Text) : null);
                // Text from the user stays the user's even when written as JSON.
                return ContainsInput(value) ? JString.Input(json) : JString.Plain(json);
            }
            case "from_json":
                try
                {
                    using var document = JsonDocument.Parse(Text(value).Text);
                    return Values.FromJson(document.RootElement, Text(value).AnyInput);
                }
                catch (JsonException) { throw new TemplateException("The chat template read JSON that is not valid."); }
            case "int":
                return value switch
                {
                    long number => number,
                    double number => (long)Math.Truncate(number),
                    bool flag => flag ? 1L : 0L,
                    JString text when long.TryParse(text.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    JString text when double.TryParse(text.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var real) => (long)Math.Truncate(real),
                    _ => arguments.Get(0, "default", 0L)
                };
            case "float":
                return value switch
                {
                    long or double or bool => Values.ToDouble(value),
                    JString text when double.TryParse(text.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var real) => real,
                    _ => arguments.Get(0, "default", 0.0)
                };
            case "abs": return value is long whole ? (object)Math.Abs(whole) : Math.Abs(Values.ToDouble(value));
            case "round":
            {
                var digits = arguments.Get(0, "precision", 0L) is long places ? (int)places : 0;
                var method = Values.Key(arguments.Get(1, "method", JString.Plain("common")));
                var number = Values.ToDouble(value);
                var factor = Math.Pow(10, digits);
                return method switch
                {
                    "ceil" => Math.Ceiling(number * factor) / factor,
                    "floor" => Math.Floor(number * factor) / factor,
                    _ => Math.Round(number, digits, MidpointRounding.AwayFromZero)
                };
            }
            case "sum":
            {
                var attribute = arguments.Get(0, "attribute");
                object? total = arguments.Get(1, "start", 0L);
                foreach (var item in Values.Iterate(value))
                {
                    var addend = attribute is null ? item : GetAttributePath(item, attribute);
                    total = total is long a && addend is long b ? (object)(a + b) : Values.ToDouble(total) + Values.ToDouble(addend);
                }
                return total;
            }
            case "min" or "max":
            {
                var items = Values.Iterate(value);
                if (items.Count == 0) return Undefined.Instance;
                var attribute = arguments.Get(1, "attribute");
                var best = items[0];
                foreach (var item in items.Skip(1))
                {
                    var compared = Values.Compare(attribute is null ? item : GetAttributePath(item, attribute),
                        attribute is null ? best : GetAttributePath(best, attribute));
                    if (name == "min" ? compared < 0 : compared > 0) best = item;
                }
                return best;
            }
            case "sort":
            {
                var reverse = Values.Truthy(arguments.Get(0, "reverse", false));
                var attribute = arguments.Get(2, "attribute");
                var items = Values.Iterate(value).ToList();
                items.Sort((x, y) => Values.Compare(attribute is null ? x : GetAttributePath(x, attribute), attribute is null ? y : GetAttributePath(y, attribute)));
                if (reverse) items.Reverse();
                return items;
            }
            case "unique":
            {
                var result = new List<object?>();
                foreach (var item in Values.Iterate(value))
                    if (!result.Any(seen => Values.Equals(seen, item))) result.Add(item);
                return result;
            }
            case "map":
            {
                var items = Values.Iterate(value);
                if (arguments.Keywords.TryGetValue("attribute", out var attribute))
                {
                    var fallback = arguments.Keywords.GetValueOrDefault("default", Undefined.Instance);
                    return items.Select(item => GetAttributePath(item, attribute) is Undefined ? fallback : GetAttributePath(item, attribute)).ToList();
                }
                var filter = Values.Key(arguments.Get(0, "filter"));
                var rest = new Arguments(arguments.Positional.Skip(1).ToList(), arguments.Keywords);
                return items.Select(item => ApplyFilter(filter, item, rest)).ToList();
            }
            case "select" or "reject":
            {
                var test = arguments.Positional.Count > 0 ? Values.Key(arguments.Positional[0]) : null;
                var testArguments = arguments.Positional.Skip(1).ToList();
                var keep = name == "select";
                return Values.Iterate(value).Where(item =>
                    (test is null ? Values.Truthy(item) : ApplyTest(test, item, testArguments)) == keep).ToList();
            }
            case "selectattr" or "rejectattr":
            {
                var attribute = arguments.Get(0, "attribute");
                var test = arguments.Positional.Count > 1 ? Values.Key(arguments.Positional[1]) : null;
                var testArguments = arguments.Positional.Skip(2).ToList();
                var keep = name == "selectattr";
                return Values.Iterate(value).Where(item =>
                {
                    var field = GetAttributePath(item, attribute);
                    return (test is null ? Values.Truthy(field) : ApplyTest(test, field, testArguments)) == keep;
                }).ToList();
            }
            case "attr": return GetAttribute(value, Values.Key(arguments.Get(0, "name")));
            case "batch":
            {
                var size = arguments.Get(0, "linecount") is long lines and > 0 ? (int)lines : 1;
                return Values.Iterate(value).Chunk(size).Select(chunk => (object?)chunk.ToList()).ToList();
            }
            case "wordcount": return (long)Text(value).Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            case "format": return JString.Plain(PercentFormat(Text(value).Text, arguments.Positional));
            case "center":
            {
                var text = Text(value).Text;
                var width = arguments.Get(0, "width", 80L) is long w ? (int)w : 80;
                var padding = Math.Max(0, width - text.Length);
                return JString.Derived(new string(' ', padding / 2) + text + new string(' ', padding - padding / 2), Text(value));
            }
            default:
                throw new TemplateException($"The chat template uses the unsupported filter '{name}'.");
        }
    }

    private bool ApplyTest(string name, object? value, List<object?> arguments)
    {
        object? Argument() => arguments.Count > 0 ? arguments[0] : Undefined.Instance;
        return name switch
        {
            "defined" => value is not Undefined,
            "undefined" => value is Undefined,
            "none" => value is null,
            "string" => value is JString,
            "mapping" => value is OrderedDictionary<string, object?> or Namespace,
            "iterable" => value is List<object?> or JString or OrderedDictionary<string, object?> or Namespace,
            "sequence" => value is List<object?> or JString or OrderedDictionary<string, object?>,
            "number" => value is long or double,
            "integer" => value is long,
            "float" => value is double,
            "boolean" => value is bool,
            "true" => value is true,
            "false" => value is false,
            "callable" => value is Callable,
            "sameas" => value is null ? Argument() is null : value is bool flag ? Argument() is bool other && flag == other : ReferenceEquals(value, Argument()),
            "eq" or "equalto" or "==" => Values.Equals(value, Argument()),
            "ne" or "!=" => !Values.Equals(value, Argument()),
            "lt" or "lessthan" or "<" => Values.Compare(value, Argument()) < 0,
            "le" or "<=" => Values.Compare(value, Argument()) <= 0,
            "gt" or "greaterthan" or ">" => Values.Compare(value, Argument()) > 0,
            "ge" or ">=" => Values.Compare(value, Argument()) >= 0,
            "in" => Contains(Argument(), value),
            "odd" => value is long odd && Math.Abs(odd % 2) == 1,
            "even" => value is long even && even % 2 == 0,
            "divisibleby" => value is long number && Argument() is long divisor && divisor != 0 && number % divisor == 0,
            "lower" => value is JString lower && lower.Text == lower.Text.ToLowerInvariant(),
            "upper" => value is JString upper && upper.Text == upper.Text.ToUpperInvariant(),
            "escaped" => false,
            _ => throw new TemplateException($"The chat template uses the unsupported test '{name}'.")
        };
    }

    private Callable? StringMethod(JString text, string name) => name switch
    {
        "strip" => Method(name, arguments => text.Trim(Characters(arguments.Get(0, "chars")))),
        "lstrip" => Method(name, arguments => text.TrimStart(Characters(arguments.Get(0, "chars")))),
        "rstrip" => Method(name, arguments => text.TrimEnd(Characters(arguments.Get(0, "chars")))),
        "upper" => Method(name, _ => text.Map(value => value.ToUpperInvariant())),
        "lower" => Method(name, _ => text.Map(value => value.ToLowerInvariant())),
        "title" => Method(name, _ => text.Map(Title)),
        "capitalize" => Method(name, _ => text.Map(Capitalize)),
        "startswith" => Method(name, arguments => Affixes(arguments.Get(0, "prefix")).Any(prefix => text.Text.StartsWith(prefix, StringComparison.Ordinal))),
        "endswith" => Method(name, arguments => Affixes(arguments.Get(0, "suffix")).Any(suffix => text.Text.EndsWith(suffix, StringComparison.Ordinal))),
        "split" => Method(name, arguments => Split(text, arguments.Get(0, "sep"), arguments.Get(1, "maxsplit", -1L) is long max ? max : -1, fromRight: false)),
        "rsplit" => Method(name, arguments => Split(text, arguments.Get(0, "sep"), arguments.Get(1, "maxsplit", -1L) is long max ? max : -1, fromRight: true)),
        "splitlines" => Method(name, _ => text.Text.Split('\n').Select(line => (object?)JString.Derived(line.TrimEnd('\r'), text)).ToList()),
        "replace" => Method(name, arguments =>
        {
            var replacement = Text(arguments.Get(1, "new"));
            var count = arguments.Get(2, "count") is long limit ? limit : -1;
            return JString.Derived(Replace(text.Text, Text(arguments.Get(0, "old")).Text, replacement.Text, count), text, replacement);
        }),
        "find" => Method(name, arguments => (long)text.Text.IndexOf(Text(arguments.Get(0, "sub")).Text, StringComparison.Ordinal)),
        "rfind" => Method(name, arguments => (long)text.Text.LastIndexOf(Text(arguments.Get(0, "sub")).Text, StringComparison.Ordinal)),
        "index" => Method(name, arguments => text.Text.IndexOf(Text(arguments.Get(0, "sub")).Text, StringComparison.Ordinal) is var found and >= 0
            ? (long)found : throw new TemplateException("The chat template looked for text that is not there.")),
        "count" => Method(name, arguments => (long)CountOf(text.Text, Text(arguments.Get(0, "sub")).Text)),
        "join" => Method(name, arguments =>
        {
            var items = Values.Iterate(arguments.Get(0, "iterable")).Select(Values.ToJString).ToList();
            var parts = new List<JString>();
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) parts.Add(text);
                parts.Add(items[i]);
            }
            return JString.Concat([.. parts]);
        }),
        "format" => Method(name, arguments => JString.Plain(BraceFormat(text.Text, arguments))),
        "removeprefix" => Method(name, arguments => Text(arguments.Get(0, "prefix")).Text is var prefix && text.Text.StartsWith(prefix, StringComparison.Ordinal)
            ? JString.Derived(text.Text[prefix.Length..], text) : text),
        "removesuffix" => Method(name, arguments => Text(arguments.Get(0, "suffix")).Text is var suffix && suffix.Length > 0 && text.Text.EndsWith(suffix, StringComparison.Ordinal)
            ? JString.Derived(text.Text[..^suffix.Length], text) : text),
        "isdigit" => Method(name, _ => text.Length > 0 && text.Text.All(char.IsDigit)),
        "isalpha" => Method(name, _ => text.Length > 0 && text.Text.All(char.IsLetter)),
        "isalnum" => Method(name, _ => text.Length > 0 && text.Text.All(char.IsLetterOrDigit)),
        "isspace" => Method(name, _ => text.Length > 0 && text.Text.All(char.IsWhiteSpace)),
        "islower" => Method(name, _ => text.Text.Any(char.IsLetter) && text.Text == text.Text.ToLowerInvariant()),
        "isupper" => Method(name, _ => text.Text.Any(char.IsLetter) && text.Text == text.Text.ToUpperInvariant()),
        _ => null
    };

    private Callable? ListMethod(List<object?> list, string name) => name switch
    {
        "append" => Method(name, arguments => { list.Add(arguments.Get(0, "item")); return null; }),
        "extend" => Method(name, arguments => { list.AddRange(Values.Iterate(arguments.Get(0, "items"))); return null; }),
        "insert" => Method(name, arguments =>
        {
            var index = arguments.Get(0, "index") is long position ? (int)Math.Clamp(position < 0 ? position + list.Count : position, 0, list.Count) : list.Count;
            list.Insert(index, arguments.Get(1, "item"));
            return null;
        }),
        "pop" => Method(name, arguments =>
        {
            if (list.Count == 0) throw new TemplateException("The chat template took an item from an empty list.");
            var index = arguments.Get(0, "index", -1L) is long position ? (int)(position < 0 ? position + list.Count : position) : list.Count - 1;
            if (index < 0 || index >= list.Count) throw new TemplateException("The chat template took an item that is not in the list.");
            var item = list[index];
            list.RemoveAt(index);
            return item;
        }),
        "index" => Method(name, arguments => list.FindIndex(item => Values.Equals(item, arguments.Get(0, "item"))) is var found and >= 0
            ? (long)found : throw new TemplateException("The chat template looked for an item that is not in the list.")),
        "count" => Method(name, arguments => (long)list.Count(item => Values.Equals(item, arguments.Get(0, "item")))),
        "reverse" => Method(name, _ => { list.Reverse(); return null; }),
        "copy" => Method(name, _ => list.ToList()),
        _ => null
    };

    private static Callable? DictionaryMethod(OrderedDictionary<string, object?> map, string name) => name switch
    {
        "get" => Method(name, arguments => map.TryGetValue(Values.Key(arguments.Get(0, "key")), out var value) ? value : arguments.Get(1, "default")),
        "items" => Method(name, _ => map.Select(pair => (object?)new List<object?> { JString.Plain(pair.Key), pair.Value }).ToList()),
        "keys" => Method(name, _ => map.Keys.Select(key => (object?)JString.Plain(key)).ToList()),
        "values" => Method(name, _ => map.Values.ToList()),
        "pop" => Method(name, arguments =>
        {
            var key = Values.Key(arguments.Get(0, "key"));
            if (map.Remove(key, out var value)) return value;
            return arguments.Positional.Count > 1 || arguments.Keywords.ContainsKey("default") ? arguments.Get(1, "default")
                : throw new TemplateException("The chat template removed a key that is not there.");
        }),
        "setdefault" => Method(name, arguments =>
        {
            var key = Values.Key(arguments.Get(0, "key"));
            if (!map.TryGetValue(key, out var value)) map[key] = value = arguments.Get(1, "default");
            return value;
        }),
        "update" => Method(name, arguments =>
        {
            if (arguments.Get(0, "other") is OrderedDictionary<string, object?> other)
                foreach (var (key, value) in other) map[key] = value;
            foreach (var (key, value) in arguments.Keywords) map[key] = value;
            return null;
        }),
        "copy" => Method(name, _ => Values.ToMap(map.Select(pair => (pair.Key, pair.Value)))),
        _ => null
    };

    private static Callable Method(string name, Func<Arguments, object?> invoke) => new(name, invoke);

    private object? GetAttributePath(object? item, object? path)
    {
        foreach (var part in Values.Key(path).Split('.'))
            item = long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && item is List<object?>
                ? GetItem(item, index) : GetAttribute(item, part);
        return item;
    }

    private static JString Text(object? value) => value switch
    {
        JString text => text,
        null or Undefined => JString.Empty,
        _ => Values.ToJString(value)
    };

    private static char[]? Characters(object? value) => value is JString { Length: > 0 } text ? text.Text.ToCharArray() : null;

    private static IEnumerable<string> Affixes(object? value) => value switch
    {
        JString text => [text.Text],
        List<object?> list => list.Select(item => Text(item).Text),
        _ => []
    };

    private static List<object?> Split(JString text, object? separator, long maximum, bool fromRight)
    {
        var source = text.Text;
        List<string> pieces;
        if (separator is null or Undefined)
        {
            pieces = fromRight ? SplitWhitespaceRight(source, maximum) : SplitWhitespace(source, maximum);
        }
        else
        {
            var marker = Text(separator).Text;
            if (marker.Length == 0) throw new TemplateException("The chat template split text on an empty separator.");
            pieces = [.. source.Split(marker)];
            if (maximum >= 0 && pieces.Count > maximum + 1)
                pieces = fromRight
                    ? [string.Join(marker, pieces.Take(pieces.Count - (int)maximum)), .. pieces.Skip(pieces.Count - (int)maximum)]
                    : [.. pieces.Take((int)maximum), string.Join(marker, pieces.Skip((int)maximum))];
        }
        return pieces.Select(piece => (object?)JString.Derived(piece, text)).ToList();
    }

    /// <summary>Python str.split() with no separator: runs of whitespace split, the rest stays whole after the limit.</summary>
    private static List<string> SplitWhitespace(string source, long maximum)
    {
        var pieces = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
            if (i >= source.Length) break;
            if (maximum >= 0 && pieces.Count == maximum) { pieces.Add(source[i..].TrimEnd()); break; }
            var start = i;
            while (i < source.Length && !char.IsWhiteSpace(source[i])) i++;
            pieces.Add(source[start..i]);
        }
        return pieces;
    }

    private static List<string> SplitWhitespaceRight(string source, long maximum)
    {
        var pieces = new List<string>();
        var i = source.Length - 1;
        while (true)
        {
            while (i >= 0 && char.IsWhiteSpace(source[i])) i--;
            if (i < 0) break;
            if (maximum >= 0 && pieces.Count == maximum) { pieces.Add(source[..(i + 1)].TrimStart()); break; }
            var end = i;
            while (i >= 0 && !char.IsWhiteSpace(source[i])) i--;
            pieces.Add(source[(i + 1)..(end + 1)]);
        }
        pieces.Reverse();
        return pieces;
    }

    private static string Replace(string text, string old, string replacement, long count)
    {
        if (count < 0) return old.Length == 0 ? text : text.Replace(old, replacement, StringComparison.Ordinal);
        var builder = new StringBuilder();
        var position = 0;
        for (var done = 0L; done < count; done++)
        {
            var found = text.IndexOf(old, position, StringComparison.Ordinal);
            if (found < 0 || old.Length == 0) break;
            builder.Append(text, position, found - position).Append(replacement);
            position = found + old.Length;
        }
        return builder.Append(text, position, text.Length - position).ToString();
    }

    private static int CountOf(string text, string part)
    {
        if (part.Length == 0) return text.Length + 1;
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Title(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousIsLetter = false;
        foreach (var c in text)
        {
            builder.Append(previousIsLetter ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
            previousIsLetter = char.IsLetter(c);
        }
        return builder.ToString();
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();

    /// <summary>Python str.format with {}, {0} and {name} fields.</summary>
    private static string BraceFormat(string format, Arguments arguments)
    {
        var builder = new StringBuilder();
        var automatic = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '{' && i + 1 < format.Length && format[i + 1] == '{') { builder.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < format.Length && format[i + 1] == '}') { builder.Append('}'); i++; continue; }
            if (c != '{') { builder.Append(c); continue; }
            var close = format.IndexOf('}', i);
            if (close < 0) throw new TemplateException("The chat template formatted text with an unclosed field.");
            var field = format[(i + 1)..close].Split(':')[0];
            object? value = field.Length == 0 ? arguments.Get(automatic++, "\0")
                : int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var position) ? arguments.Get(position, "\0")
                : arguments.Keywords.GetValueOrDefault(field, Undefined.Instance);
            builder.Append(Values.ToJString(value).Text);
            i = close;
        }
        return builder.ToString();
    }

    private static bool ContainsInput(object? value) => value switch
    {
        JString text => text.AnyInput,
        List<object?> list => list.Any(ContainsInput),
        OrderedDictionary<string, object?> map => map.Values.Any(ContainsInput),
        _ => false
    };

    /// <summary>Python json.dumps with ensure_ascii off, as Hugging Face's tojson filter writes it.</summary>
    private static string Json(object? value, int? indent, bool sortKeys, (string Item, string Key)? separators)
    {
        var (itemSeparator, keySeparator) = separators ?? (indent is null ? (", ", ": ") : (",", ": "));
        var builder = new StringBuilder();
        void Write(object? item, int level)
        {
            switch (item)
            {
                case null or Undefined: builder.Append("null"); break;
                case bool flag: builder.Append(flag ? "true" : "false"); break;
                case long number: builder.Append(number.ToString(CultureInfo.InvariantCulture)); break;
                case double number: builder.Append(double.IsFinite(number) ? Values.FormatDouble(number) : number > 0 ? "Infinity" : number < 0 ? "-Infinity" : "NaN"); break;
                case JString text: WriteString(builder, text.Text); break;
                case List<object?> list:
                    WriteContainer('[', ']', list.Count, level, i => Write(list[i], level + 1));
                    break;
                case OrderedDictionary<string, object?> or Namespace:
                {
                    var map = item as OrderedDictionary<string, object?> ?? ((Namespace)item).Values;
                    var pairs = sortKeys ? map.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList() : map.ToList();
                    WriteContainer('{', '}', pairs.Count, level, i =>
                    {
                        WriteString(builder, pairs[i].Key);
                        builder.Append(keySeparator);
                        Write(pairs[i].Value, level + 1);
                    });
                    break;
                }
                default: WriteString(builder, Values.Repr(item)); break;
            }
        }
        void WriteContainer(char open, char close, int count, int level, Action<int> writeItem)
        {
            builder.Append(open);
            if (count == 0) { builder.Append(close); return; }
            for (var i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(itemSeparator);
                if (indent is { } spaces) builder.Append('\n').Append(' ', spaces * (level + 1));
                writeItem(i);
            }
            if (indent is { } width) builder.Append('\n').Append(' ', width * level);
            builder.Append(close);
        }
        Write(value, 0);
        return builder.ToString();
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
