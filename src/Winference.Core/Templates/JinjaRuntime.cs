using System.Globalization;
using System.Text;

namespace Winference.Core.Templates;

/// <summary>Runs a parsed template. Variables live in a stack of scopes, as in Jinja: a loop or macro opens one.</summary>
internal sealed partial class JinjaRuntime
{
    private const int MaximumOutput = 4 * 1024 * 1024;
    private const int MaximumDepth = 64;
    private readonly List<Dictionary<string, object?>> _scopes = [];
    private readonly Dictionary<string, object?> _globals;
    private int _depth;
    private int _steps;
    private long _written;

    private enum Flow { Normal, Break, Continue }

    public JinjaRuntime(Dictionary<string, object?> variables)
    {
        _globals = new(StringComparer.Ordinal)
        {
            ["range"] = new Callable("range", Range),
            ["namespace"] = new Callable("namespace", CreateNamespace),
            ["dict"] = new Callable("dict", arguments => Values.ToMap(arguments.Keywords.Select(pair => (pair.Key, pair.Value)))),
            ["raise_exception"] = new Callable("raise_exception", arguments =>
                throw new TemplateException(Values.ToJString(arguments.Get(0, "message")).Text)),
            ["strftime_now"] = new Callable("strftime_now", arguments =>
                JString.Plain(StrftimeNow(Values.ToJString(arguments.Get(0, "format")).Text))),
        };
        foreach (var (name, value) in variables) _globals[name] = value;
        _scopes.Add(new(StringComparer.Ordinal));
    }

    /// <summary>Clock for strftime_now, replaceable by tests.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    public JString Render(List<Node> nodes)
    {
        var output = new List<JString>();
        Run(nodes, output);
        return JString.Concat([.. output]);
    }

    private Flow Run(List<Node> nodes, List<JString> output)
    {
        foreach (var node in nodes)
        {
            if (++_steps > 5_000_000) throw new TemplateException("The chat template ran for too long.");
            switch (node)
            {
                case TextNode text:
                    Write(output, JString.Plain(text.Text));
                    break;
                case OutputNode print:
                    Write(output, Values.ToJString(Evaluate(print.Value)));
                    break;
                case IfNode branch:
                {
                    var taken = branch.Branches.FirstOrDefault(item => Values.Truthy(Evaluate(item.Condition))).Body ?? branch.Else;
                    if (taken is not null)
                    {
                        var flow = Run(taken, output);
                        if (flow != Flow.Normal) return flow;
                    }
                    break;
                }
                case ForNode loop:
                    RunFor(loop, output);
                    break;
                case SetNode set:
                    Assign(set.Target, set.Value is not null ? Evaluate(set.Value) : Capture(set.Body!));
                    break;
                case MacroNode macro:
                    _scopes[^1][macro.Name] = CreateMacro(macro);
                    break;
                case FilterBlockNode block:
                    Write(output, Values.ToJString(ApplyFilter(block.Filter, Capture(block.Body),
                        new(block.Arguments.Select(Evaluate).ToList(), []))));
                    break;
                case ScopeNode scope:
                {
                    var flow = Run(scope.Body, output);
                    if (flow != Flow.Normal) return flow;
                    break;
                }
                case DoNode work:
                    Evaluate(work.Value);
                    break;
                case BreakNode: return Flow.Break;
                case ContinueNode: return Flow.Continue;
            }
        }
        return Flow.Normal;
    }

    private void Write(List<JString> output, JString text)
    {
        if (text.Length == 0) return;
        output.Add(text);
        if ((_written += text.Length) > MaximumOutput) throw new TemplateException("The chat template produced too much text.");
    }

    private JString Capture(List<Node> body)
    {
        var output = new List<JString>();
        Run(body, output);
        return JString.Concat([.. output]);
    }

    private void RunFor(ForNode loop, List<JString> output)
    {
        var source = Values.Iterate(Evaluate(loop.Items));
        var items = new List<object?>();
        _scopes.Add(new(StringComparer.Ordinal));
        try
        {
            foreach (var item in source)
            {
                if (loop.Filter is null) { items.Add(item); continue; }
                Bind(loop.Targets, item);
                if (Values.Truthy(Evaluate(loop.Filter))) items.Add(item);
            }
            if (items.Count == 0)
            {
                if (loop.Else is not null) Run(loop.Else, output);
                return;
            }
            for (var i = 0; i < items.Count; i++)
            {
                Bind(loop.Targets, items[i]);
                var index = i;
                _scopes[^1]["loop"] = Values.ToMap([
                    ("index", (long)(i + 1)), ("index0", (long)i),
                    ("revindex", (long)(items.Count - i)), ("revindex0", (long)(items.Count - i - 1)),
                    ("first", i == 0), ("last", i == items.Count - 1), ("length", (long)items.Count), ("depth", 1L), ("depth0", 0L),
                    ("previtem", i > 0 ? items[i - 1] : Undefined.Instance),
                    ("nextitem", i < items.Count - 1 ? items[i + 1] : Undefined.Instance),
                    ("cycle", new Callable("cycle", arguments =>
                        arguments.Positional.Count == 0 ? null : arguments.Positional[index % arguments.Positional.Count]))
                ]);
                if (Run(loop.Body, output) == Flow.Break) break;
            }
        }
        finally { _scopes.RemoveAt(_scopes.Count - 1); }
    }

    private void Bind(List<string> targets, object? item)
    {
        if (targets.Count == 1) { _scopes[^1][targets[0]] = item; return; }
        var values = Values.Iterate(item);
        if (values.Count != targets.Count) throw new TemplateException("The chat template unpacked the wrong number of values in a loop.");
        for (var i = 0; i < targets.Count; i++) _scopes[^1][targets[i]] = values[i];
    }

    private void Assign(Expr target, object? value)
    {
        switch (target)
        {
            case NameExpr name:
                _scopes[^1][name.Name] = value;
                break;
            case AttributeExpr { Target: var owner, Name: var attribute }:
                switch (Evaluate(owner))
                {
                    case Namespace space: space.Values[attribute] = value; break;
                    case OrderedDictionary<string, object?> map: map[attribute] = value; break;
                    default: throw new TemplateException("The chat template set an attribute on a value that cannot hold one.");
                }
                break;
            case TupleExpr tuple:
                var values = Values.Iterate(value);
                if (values.Count != tuple.Items.Count) throw new TemplateException("The chat template unpacked the wrong number of values.");
                for (var i = 0; i < values.Count; i++) Assign(tuple.Items[i], values[i]);
                break;
            default:
                throw new TemplateException("The chat template assigned to something that is not a variable.");
        }
    }

    private Callable CreateMacro(MacroNode macro)
    {
        var defining = _scopes[0];
        return new Callable(macro.Name, arguments =>
        {
            if (++_depth > MaximumDepth) throw new TemplateException("The chat template calls itself too deeply.");
            var saved = _scopes.ToList();
            try
            {
                _scopes.Clear();
                _scopes.Add(defining);
                var frame = new Dictionary<string, object?>(StringComparer.Ordinal);
                _scopes.Add(frame);
                for (var i = 0; i < macro.Parameters.Count; i++)
                {
                    var (name, fallback) = macro.Parameters[i];
                    frame[name] = arguments.Keywords.TryGetValue(name, out var keyword) ? keyword
                        : i < arguments.Positional.Count ? arguments.Positional[i]
                        : fallback is not null ? Evaluate(fallback) : Undefined.Instance;
                }
                frame["varargs"] = arguments.Positional.Skip(macro.Parameters.Count).ToList();
                frame["kwargs"] = Values.ToMap(arguments.Keywords.Where(pair => macro.Parameters.All(parameter => parameter.Name != pair.Key))
                    .Select(pair => (pair.Key, pair.Value)));
                return Capture(macro.Body);
            }
            finally
            {
                _scopes.Clear();
                _scopes.AddRange(saved);
                _depth--;
            }
        });
    }

    private object? Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var value)) return value;
        return _globals.TryGetValue(name, out var global) ? global : Undefined.Instance;
    }

    private object? Evaluate(Expr expression)
    {
        switch (expression)
        {
            case Literal literal:
                return literal.Value is string text ? JString.Plain(text) : literal.Value;
            case NameExpr name:
                return Lookup(name.Name);
            case ListExpr list:
                return list.Items.Select(Evaluate).ToList();
            case TupleExpr tuple:
                return tuple.Items.Select(Evaluate).ToList();
            case DictExpr dict:
                return Values.ToMap(dict.Items.Select(item => (Values.Key(Evaluate(item.Key)), Evaluate(item.Value))));
            case AttributeExpr attribute:
                return GetAttribute(Evaluate(attribute.Target), attribute.Name);
            case IndexExpr index:
                return GetItem(Evaluate(index.Target), Evaluate(index.Index));
            case SliceExpr slice:
                return Slice(Evaluate(slice.Target), Optional(slice.Start), Optional(slice.Stop), Optional(slice.Step));
            case CallExpr call:
            {
                var target = Evaluate(call.Target);
                if (target is not Callable callable)
                    throw new TemplateException("The chat template called something that is not a function.");
                return callable.Invoke(EvaluateArguments(call.Arguments, call.Keywords));
            }
            case FilterExpr filter:
                return ApplyFilter(filter.Name, Evaluate(filter.Value), EvaluateArguments(filter.Arguments, filter.Keywords));
            case TestExpr test:
            {
                var result = ApplyTest(test.Name, Evaluate(test.Value), test.Arguments.Select(Evaluate).ToList());
                return test.Negated ? !result : result;
            }
            case UnaryExpr unary:
            {
                var operand = Evaluate(unary.Operand);
                return unary.Operator switch
                {
                    "not" => !Values.Truthy(operand),
                    "-" => operand switch
                    {
                        long number => (object)(-number),
                        double number => -number,
                        _ => throw new TemplateException("The chat template negated a value that is not a number.")
                    },
                    _ => operand
                };
            }
            case BinaryExpr binary:
                return EvaluateBinary(binary);
            case ConditionalExpr conditional:
                return Values.Truthy(Evaluate(conditional.Condition)) ? Evaluate(conditional.Then)
                    : conditional.Else is not null ? Evaluate(conditional.Else) : Undefined.Instance;
            default:
                throw new TemplateException("The chat template contains an unsupported expression.");
        }
    }

    private long? Optional(Expr? expression) => expression is null ? null : Evaluate(expression) switch
    {
        long number => number,
        null or Undefined => null,
        _ => throw new TemplateException("The chat template sliced with a value that is not a whole number.")
    };

    private Arguments EvaluateArguments(List<Expr> positional, List<(string Name, Expr Value)> keywords) =>
        new(positional.Select(Evaluate).ToList(), keywords.ToDictionary(item => item.Name, item => Evaluate(item.Value), StringComparer.Ordinal));

    private object? EvaluateBinary(BinaryExpr binary)
    {
        if (binary.Operator == "and")
        {
            var left = Evaluate(binary.Left);
            return Values.Truthy(left) ? Evaluate(binary.Right) : left;
        }
        if (binary.Operator == "or")
        {
            var left = Evaluate(binary.Left);
            return Values.Truthy(left) ? left : Evaluate(binary.Right);
        }
        var a = Evaluate(binary.Left);
        var b = Evaluate(binary.Right);
        switch (binary.Operator)
        {
            case "==": return Values.Equals(a, b);
            case "!=": return !Values.Equals(a, b);
            case "<": return Values.Compare(a, b) < 0;
            case "<=": return Values.Compare(a, b) <= 0;
            case ">": return Values.Compare(a, b) > 0;
            case ">=": return Values.Compare(a, b) >= 0;
            case "in": return Contains(b, a);
            case "not in": return !Contains(b, a);
            case "~": return JString.Concat(Values.ToJString(a), Values.ToJString(b));
            case "+":
                return (a, b) switch
                {
                    (JString x, JString y) => JString.Concat(x, y),
                    // A missing variable adds as nothing, as llama.cpp does, so optional template inputs can be left out.
                    (JString x, Undefined) => x,
                    (Undefined, JString y) => y,
                    (List<object?> x, List<object?> y) => x.Concat(y).ToList(),
                    (long x, long y) => x + y,
                    _ when Values.IsNumber(a) && Values.IsNumber(b) => Values.ToDouble(a) + Values.ToDouble(b),
                    _ => throw new TemplateException("The chat template added values of different kinds.")
                };
            case "-":
                return a is long x1 && b is long y1 ? (object)(x1 - y1) : Values.ToDouble(a) - Values.ToDouble(b);
            case "*":
                return (a, b) switch
                {
                    (JString text, long count) => JString.Concat([.. Enumerable.Repeat(text, (int)Math.Clamp(count, 0, 100_000))]),
                    (List<object?> list, long count) => Enumerable.Repeat(list, (int)Math.Clamp(count, 0, 10_000)).SelectMany(item => item).ToList(),
                    (long x, long y) => x * y,
                    _ => Values.ToDouble(a) * Values.ToDouble(b)
                };
            case "/":
                return Values.ToDouble(b) == 0 ? throw new TemplateException("The chat template divided by zero.") : Values.ToDouble(a) / Values.ToDouble(b);
            case "//":
                if (a is long n1 && b is long d1)
                    return d1 == 0 ? throw new TemplateException("The chat template divided by zero.") : (long)Math.Floor((double)n1 / d1);
                return Math.Floor(Values.ToDouble(a) / Values.ToDouble(b));
            case "%":
                if (a is JString format) return JString.Plain(PercentFormat(format.Text, b));
                if (a is long n2 && b is long d2)
                    return d2 == 0 ? throw new TemplateException("The chat template divided by zero.") : ((n2 % d2) + d2) % d2;
                var divisor = Values.ToDouble(b);
                var dividend = Values.ToDouble(a);
                return dividend - divisor * Math.Floor(dividend / divisor);
            case "**":
                if (a is long baseValue && b is long exponent && exponent >= 0) return (long)Math.Pow(baseValue, exponent);
                return Math.Pow(Values.ToDouble(a), Values.ToDouble(b));
            default:
                throw new TemplateException("The chat template used an unsupported operator.");
        }
    }

    private static bool Contains(object? container, object? item) => container switch
    {
        JString text => item is JString part ? text.Text.Contains(part.Text, StringComparison.Ordinal)
            : throw new TemplateException("The chat template searched text for a value that is not text."),
        List<object?> list => list.Any(element => Values.Equals(element, item)),
        OrderedDictionary<string, object?> map => map.ContainsKey(Values.Key(item)),
        Namespace space => space.Values.ContainsKey(Values.Key(item)),
        null or Undefined => false,
        _ => throw new TemplateException("The chat template searched a value that cannot contain others.")
    };

    private object? GetAttribute(object? owner, string name)
    {
        switch (owner)
        {
            case OrderedDictionary<string, object?> map:
                if (map.TryGetValue(name, out var value)) return value;
                return DictionaryMethod(map, name) ?? (object)Undefined.Instance;
            case Namespace space:
                return space.Values.TryGetValue(name, out var stored) ? stored : Undefined.Instance;
            case JString text:
                return StringMethod(text, name) ?? (object)Undefined.Instance;
            case List<object?> list:
                return ListMethod(list, name) ?? (object)Undefined.Instance;
            default:
                return Undefined.Instance;
        }
    }

    private static object? GetItem(object? owner, object? key)
    {
        switch (owner)
        {
            case OrderedDictionary<string, object?> map:
                return map.TryGetValue(Values.Key(key), out var value) ? value : Undefined.Instance;
            case Namespace space:
                return space.Values.TryGetValue(Values.Key(key), out var stored) ? stored : Undefined.Instance;
            case List<object?> list when key is long index:
                if (index < 0) index += list.Count;
                return index >= 0 && index < list.Count ? list[(int)index] : Undefined.Instance;
            case JString text when key is long position:
                if (position < 0) position += text.Length;
                return position >= 0 && position < text.Length ? JString.Derived(text.Text[(int)position].ToString(), text) : Undefined.Instance;
            default:
                return Undefined.Instance;
        }
    }

    private static object? Slice(object? owner, long? start, long? stop, long? step)
    {
        var count = owner switch
        {
            List<object?> list => list.Count,
            JString text => text.Length,
            null or Undefined => 0,
            _ => throw new TemplateException("The chat template sliced a value that is not a list or text.")
        };
        var stride = step ?? 1;
        if (stride == 0) throw new TemplateException("The chat template sliced with a step of zero.");
        static long Clamp(long value, long low, long high) => Math.Min(Math.Max(value, low), high);
        long first, last;
        if (stride > 0)
        {
            first = start is { } s ? Clamp(s < 0 ? s + count : s, 0, count) : 0;
            last = stop is { } e ? Clamp(e < 0 ? e + count : e, 0, count) : count;
        }
        else
        {
            first = start is { } s ? Clamp(s < 0 ? s + count : s, -1, count - 1) : count - 1;
            last = stop is { } e ? Clamp(e < 0 ? e + count : e, -1, count - 1) : -1;
        }
        var indexes = new List<int>();
        for (var i = first; stride > 0 ? i < last : i > last; i += stride) indexes.Add((int)i);
        return owner switch
        {
            List<object?> list => indexes.Select(i => list[i]).ToList(),
            JString text => JString.Derived(new string([.. indexes.Select(i => text.Text[i])]), text),
            _ => new List<object?>()
        };
    }

    private object? Range(Arguments arguments)
    {
        var numbers = arguments.Positional.Select(value => value is long number ? number
            : throw new TemplateException("The chat template called range with a value that is not a whole number.")).ToList();
        var (start, stop, step) = numbers.Count switch
        {
            1 => (0L, numbers[0], 1L),
            2 => (numbers[0], numbers[1], 1L),
            3 => (numbers[0], numbers[1], numbers[2]),
            _ => throw new TemplateException("The chat template called range with the wrong number of values.")
        };
        if (step == 0) throw new TemplateException("The chat template called range with a step of zero.");
        var result = new List<object?>();
        for (var i = start; step > 0 ? i < stop : i > stop; i += step)
        {
            result.Add(i);
            if (result.Count > 100_000) throw new TemplateException("The chat template asked for too large a range.");
        }
        return result;
    }

    private static object? CreateNamespace(Arguments arguments)
    {
        var space = new Namespace();
        foreach (var value in arguments.Positional)
            if (value is OrderedDictionary<string, object?> map)
                foreach (var (key, item) in map) space.Values[key] = item;
        foreach (var (key, value) in arguments.Keywords) space.Values[key] = value;
        return space;
    }

    private string StrftimeNow(string format)
    {
        var now = Now();
        var builder = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length) { builder.Append(format[i]); continue; }
            var code = format[++i];
            var culture = CultureInfo.InvariantCulture;
            builder.Append(code switch
            {
                'Y' => now.ToString("yyyy", culture),
                'y' => now.ToString("yy", culture),
                'm' => now.ToString("MM", culture),
                'd' => now.ToString("dd", culture),
                'e' => now.Day.ToString(culture).PadLeft(2),
                '-' when i + 1 < format.Length && format[i + 1] == 'd' => (++i, now.Day.ToString(culture)).Item2,
                'H' => now.ToString("HH", culture),
                'I' => now.ToString("hh", culture),
                'M' => now.ToString("mm", culture),
                'S' => now.ToString("ss", culture),
                'p' => now.ToString("tt", culture),
                'b' => now.ToString("MMM", culture),
                'B' => now.ToString("MMMM", culture),
                'a' => now.ToString("ddd", culture),
                'A' => now.ToString("dddd", culture),
                'j' => now.DayOfYear.ToString("000", culture),
                'Z' => "",
                'z' => "",
                '%' => "%",
                _ => "%" + code
            });
        }
        return builder.ToString();
    }

    private static string PercentFormat(string format, object? values)
    {
        var items = values is List<object?> list ? list : [values];
        var next = 0;
        var builder = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length) { builder.Append(format[i]); continue; }
            var code = format[++i];
            if (code == '%') { builder.Append('%'); continue; }
            if (next >= items.Count) throw new TemplateException("The chat template formatted text with too few values.");
            var value = items[next++];
            builder.Append(code switch
            {
                'd' or 'i' => ((long)Values.ToDouble(value)).ToString(CultureInfo.InvariantCulture),
                'r' => Values.Repr(value),
                _ => Values.ToJString(value).Text
            });
        }
        return builder.ToString();
    }
}
