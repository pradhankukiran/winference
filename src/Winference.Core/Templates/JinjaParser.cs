using System.Globalization;

namespace Winference.Core.Templates;

internal abstract record Node;
internal sealed record TextNode(string Text) : Node;
internal sealed record OutputNode(Expr Value) : Node;
internal sealed record IfNode(List<(Expr Condition, List<Node> Body)> Branches, List<Node>? Else) : Node;
internal sealed record ForNode(List<string> Targets, Expr Items, Expr? Filter, List<Node> Body, List<Node>? Else) : Node;
internal sealed record SetNode(Expr Target, Expr? Value, List<Node>? Body) : Node;
internal sealed record MacroNode(string Name, List<(string Name, Expr? Default)> Parameters, List<Node> Body) : Node;
internal sealed record FilterBlockNode(string Filter, List<Expr> Arguments, List<Node> Body) : Node;
internal sealed record ScopeNode(List<Node> Body) : Node;
internal sealed record BreakNode : Node;
internal sealed record ContinueNode : Node;
internal sealed record DoNode(Expr Value) : Node;

internal abstract record Expr;
internal sealed record Literal(object? Value) : Expr;
internal sealed record NameExpr(string Name) : Expr;
internal sealed record ListExpr(List<Expr> Items) : Expr;
internal sealed record TupleExpr(List<Expr> Items) : Expr;
internal sealed record DictExpr(List<(Expr Key, Expr Value)> Items) : Expr;
internal sealed record AttributeExpr(Expr Target, string Name) : Expr;
internal sealed record IndexExpr(Expr Target, Expr Index) : Expr;
internal sealed record SliceExpr(Expr Target, Expr? Start, Expr? Stop, Expr? Step) : Expr;
internal sealed record CallExpr(Expr Target, List<Expr> Arguments, List<(string Name, Expr Value)> Keywords) : Expr;
internal sealed record FilterExpr(Expr Value, string Name, List<Expr> Arguments, List<(string Name, Expr Value)> Keywords) : Expr;
internal sealed record TestExpr(Expr Value, string Name, List<Expr> Arguments, bool Negated) : Expr;
internal sealed record UnaryExpr(string Operator, Expr Operand) : Expr;
internal sealed record BinaryExpr(string Operator, Expr Left, Expr Right) : Expr;
internal sealed record ConditionalExpr(Expr Condition, Expr Then, Expr? Else) : Expr;

/// <summary>Builds the template's statement tree from the lexer's tokens.</summary>
internal sealed class JinjaParser(List<Token> tokens)
{
    private int _index;

    public static List<Node> Parse(string source)
    {
        var parser = new JinjaParser(JinjaLexer.Tokenize(source));
        var body = parser.ParseBody([]);
        if (parser.Current.Kind != TokenKind.End) throw parser.Error("Unexpected end tag");
        return body;
    }

    private Token Current => tokens[_index];
    private Token Peek(int offset = 1) => tokens[Math.Min(_index + offset, tokens.Count - 1)];
    private Token Next() => tokens[_index++];

    private TemplateException Error(string what) =>
        new($"{what} in the chat template at position {Current.Position}.");

    /// <summary>Parses until one of the given block keywords (left unconsumed) or the end.</summary>
    private List<Node> ParseBody(string[] stopAt)
    {
        var nodes = new List<Node>();
        while (true)
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.End:
                    if (stopAt.Length > 0) throw Error($"Missing {{% {stopAt[0]} %}}");
                    return nodes;
                case TokenKind.Text:
                    Next();
                    nodes.Add(new TextNode(token.Value));
                    break;
                case TokenKind.OutputOpen:
                    Next();
                    var value = ParseExpression();
                    Expect(TokenKind.OutputClose);
                    nodes.Add(new OutputNode(value));
                    break;
                case TokenKind.BlockOpen:
                    var keyword = Peek().Kind == TokenKind.Name ? Peek().Value : "";
                    if (stopAt.Contains(keyword)) return nodes;
                    Next();
                    nodes.Add(ParseStatement());
                    break;
                default:
                    throw Error("Unexpected token");
            }
        }
    }

    private Node ParseStatement()
    {
        var keyword = ExpectName();
        switch (keyword)
        {
            case "if": return ParseIf();
            case "for": return ParseFor();
            case "set": return ParseSet();
            case "macro": return ParseMacro();
            case "filter": return ParseFilterBlock();
            case "generation":
            {
                Expect(TokenKind.BlockClose);
                var body = ParseBody(["endgeneration"]);
                EndBlock("endgeneration");
                return new ScopeNode(body);
            }
            case "break": Expect(TokenKind.BlockClose); return new BreakNode();
            case "continue": Expect(TokenKind.BlockClose); return new ContinueNode();
            case "do":
            {
                var value = ParseExpression();
                Expect(TokenKind.BlockClose);
                return new DoNode(value);
            }
            default: throw Error($"Unsupported statement '{keyword}'");
        }
    }

    private Node ParseIf()
    {
        var branches = new List<(Expr, List<Node>)>();
        var condition = ParseExpression();
        Expect(TokenKind.BlockClose);
        List<Node>? otherwise = null;
        while (true)
        {
            var body = ParseBody(["elif", "else", "endif"]);
            branches.Add((condition, body));
            Expect(TokenKind.BlockOpen);
            var keyword = ExpectName();
            if (keyword == "elif")
            {
                condition = ParseExpression();
                Expect(TokenKind.BlockClose);
                continue;
            }
            if (keyword == "else")
            {
                Expect(TokenKind.BlockClose);
                otherwise = ParseBody(["endif"]);
                EndBlock("endif");
            }
            else Expect(TokenKind.BlockClose);
            return new IfNode(branches, otherwise);
        }
    }

    private Node ParseFor()
    {
        var targets = new List<string> { ExpectName() };
        while (IsOperator(","))
        {
            Next();
            targets.Add(ExpectName());
        }
        if (ExpectName() != "in") throw Error("Expected 'in'");
        var items = ParseExpression(allowConditional: false);
        Expr? filter = null;
        if (IsName("if"))
        {
            Next();
            filter = ParseExpression(allowConditional: false);
        }
        if (IsName("recursive")) throw Error("Recursive loops are not supported");
        Expect(TokenKind.BlockClose);
        var body = ParseBody(["else", "endfor"]);
        List<Node>? otherwise = null;
        Expect(TokenKind.BlockOpen);
        if (ExpectName() == "else")
        {
            Expect(TokenKind.BlockClose);
            otherwise = ParseBody(["endfor"]);
            EndBlock("endfor");
        }
        else Expect(TokenKind.BlockClose);
        return new ForNode(targets, items, filter, body, otherwise);
    }

    private Node ParseSet()
    {
        var target = ParsePrimary();
        while (IsOperator("."))
        {
            Next();
            target = new AttributeExpr(target, ExpectName());
        }
        if (IsOperator(","))
        {
            var names = new List<Expr> { target };
            while (IsOperator(",")) { Next(); names.Add(ParsePrimary()); }
            target = new TupleExpr(names);
        }
        if (Current.Kind == TokenKind.BlockClose)
        {
            Next();
            var body = ParseBody(["endset"]);
            EndBlock("endset");
            return new SetNode(target, null, body);
        }
        ExpectOperator("=");
        var value = ParseExpression();
        if (IsOperator(","))
        {
            var items = new List<Expr> { value };
            while (IsOperator(",")) { Next(); items.Add(ParseExpression()); }
            value = new TupleExpr(items);
        }
        Expect(TokenKind.BlockClose);
        return new SetNode(target, value, null);
    }

    private Node ParseMacro()
    {
        var name = ExpectName();
        ExpectOperator("(");
        var parameters = new List<(string, Expr?)>();
        while (!IsOperator(")"))
        {
            var parameter = ExpectName();
            Expr? fallback = null;
            if (IsOperator("=")) { Next(); fallback = ParseExpression(); }
            parameters.Add((parameter, fallback));
            if (!IsOperator(",")) break;
            Next();
        }
        ExpectOperator(")");
        Expect(TokenKind.BlockClose);
        var body = ParseBody(["endmacro"]);
        EndBlock("endmacro");
        return new MacroNode(name, parameters, body);
    }

    private Node ParseFilterBlock()
    {
        var name = ExpectName();
        var arguments = new List<Expr>();
        if (IsOperator("("))
        {
            var (positional, _) = ParseArguments();
            arguments = positional;
        }
        Expect(TokenKind.BlockClose);
        var body = ParseBody(["endfilter"]);
        EndBlock("endfilter");
        return new FilterBlockNode(name, arguments, body);
    }

    private void EndBlock(string keyword)
    {
        Expect(TokenKind.BlockOpen);
        if (ExpectName() != keyword) throw Error($"Expected {{% {keyword} %}}");
        Expect(TokenKind.BlockClose);
    }

    public Expr ParseExpression(bool allowConditional = true)
    {
        var value = ParseOr();
        if (allowConditional && IsName("if"))
        {
            Next();
            var condition = ParseOr();
            Expr? otherwise = null;
            if (IsName("else")) { Next(); otherwise = ParseExpression(); }
            return new ConditionalExpr(condition, value, otherwise);
        }
        return value;
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (IsName("or")) { Next(); left = new BinaryExpr("or", left, ParseAnd()); }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (IsName("and")) { Next(); left = new BinaryExpr("and", left, ParseNot()); }
        return left;
    }

    private Expr ParseNot()
    {
        if (IsName("not")) { Next(); return new UnaryExpr("not", ParseNot()); }
        return ParseCompare();
    }

    private Expr ParseCompare()
    {
        var left = ParseConcat();
        while (true)
        {
            if (Current.Kind == TokenKind.Operator && Current.Value is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                var op = Next().Value;
                left = new BinaryExpr(op, left, ParseConcat());
            }
            else if (IsName("in")) { Next(); left = new BinaryExpr("in", left, ParseConcat()); }
            else if (IsName("not") && Peek().Kind == TokenKind.Name && Peek().Value == "in")
            {
                Next(); Next();
                left = new BinaryExpr("not in", left, ParseConcat());
            }
            else return left;
        }
    }

    private Expr ParseConcat()
    {
        var left = ParseAdd();
        while (IsOperator("~")) { Next(); left = new BinaryExpr("~", left, ParseAdd()); }
        return left;
    }

    private Expr ParseAdd()
    {
        var left = ParseMultiply();
        while (Current.Kind == TokenKind.Operator && Current.Value is "+" or "-")
        {
            var op = Next().Value;
            left = new BinaryExpr(op, left, ParseMultiply());
        }
        return left;
    }

    private Expr ParseMultiply()
    {
        var left = ParsePower();
        while (Current.Kind == TokenKind.Operator && Current.Value is "*" or "/" or "//" or "%")
        {
            var op = Next().Value;
            left = new BinaryExpr(op, left, ParsePower());
        }
        return left;
    }

    private Expr ParsePower()
    {
        var left = ParseUnary();
        while (IsOperator("**")) { Next(); left = new BinaryExpr("**", left, ParseUnary()); }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Current.Kind == TokenKind.Operator && Current.Value is "-" or "+")
        {
            var op = Next().Value;
            return new UnaryExpr(op, ParseUnary());
        }
        var value = ParsePostfix(ParsePrimary());
        while (true)
        {
            if (IsOperator("|"))
            {
                Next();
                var name = ParseDottedName();
                var (positional, keywords) = IsOperator("(") ? ParseArguments() : ([], []);
                value = ParsePostfix(new FilterExpr(value, name, positional, keywords));
            }
            else if (IsName("is"))
            {
                Next();
                var negated = false;
                if (IsName("not")) { Next(); negated = true; }
                var name = ParseDottedName();
                var arguments = new List<Expr>();
                if (IsOperator("(")) arguments = ParseArguments().Positional;
                else if (StartsTestArgument()) arguments.Add(ParsePostfix(ParsePrimary()));
                value = new TestExpr(value, name, arguments, negated);
            }
            else return value;
        }
    }

    private bool StartsTestArgument() => Current.Kind switch
    {
        TokenKind.String or TokenKind.Integer or TokenKind.Float => true,
        TokenKind.Name => Current.Value is not ("and" or "or" or "else" or "if" or "in" or "is" or "not"),
        TokenKind.Operator => Current.Value is "[" or "{",
        _ => false
    };

    private string ParseDottedName()
    {
        var name = ExpectName();
        while (IsOperator(".") && Peek().Kind == TokenKind.Name) { Next(); name += "." + ExpectName(); }
        return name;
    }

    private Expr ParsePostfix(Expr value)
    {
        while (true)
        {
            if (IsOperator("."))
            {
                Next();
                if (Current.Kind == TokenKind.Integer)
                    value = new IndexExpr(value, new Literal(long.Parse(Next().Value, CultureInfo.InvariantCulture)));
                else value = new AttributeExpr(value, ExpectName());
            }
            else if (IsOperator("["))
            {
                Next();
                value = ParseSubscript(value);
            }
            else if (IsOperator("("))
            {
                var (positional, keywords) = ParseArguments();
                value = new CallExpr(value, positional, keywords);
            }
            else return value;
        }
    }

    private Expr ParseSubscript(Expr target)
    {
        Expr? start = null, stop = null, step = null;
        if (!IsOperator(":")) start = ParseExpression();
        if (!IsOperator(":"))
        {
            ExpectOperator("]");
            return new IndexExpr(target, start!);
        }
        Next();
        if (!IsOperator(":") && !IsOperator("]")) stop = ParseExpression();
        if (IsOperator(":"))
        {
            Next();
            if (!IsOperator("]")) step = ParseExpression();
        }
        ExpectOperator("]");
        return new SliceExpr(target, start, stop, step);
    }

    private (List<Expr> Positional, List<(string, Expr)> Keywords) ParseArguments()
    {
        ExpectOperator("(");
        var positional = new List<Expr>();
        var keywords = new List<(string, Expr)>();
        while (!IsOperator(")"))
        {
            if (Current.Kind == TokenKind.Name && Peek().Kind == TokenKind.Operator && Peek().Value == "=")
            {
                var name = Next().Value;
                Next();
                keywords.Add((name, ParseExpression()));
            }
            else positional.Add(ParseExpression());
            if (!IsOperator(",")) break;
            Next();
        }
        ExpectOperator(")");
        return (positional, keywords);
    }

    private Expr ParsePrimary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case TokenKind.String:
                var text = token.Value;
                while (Current.Kind == TokenKind.String) text += Next().Value; // adjacent strings join
                return new Literal(text);
            case TokenKind.Integer:
                return new Literal(long.TryParse(token.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var whole)
                    ? (object)whole : double.Parse(token.Value, CultureInfo.InvariantCulture));
            case TokenKind.Float:
                return new Literal(double.Parse(token.Value, CultureInfo.InvariantCulture));
            case TokenKind.Name:
                return token.Value switch
                {
                    "true" or "True" => new Literal(true),
                    "false" or "False" => new Literal(false),
                    "none" or "None" => new Literal(null),
                    _ => new NameExpr(token.Value)
                };
            case TokenKind.Operator when token.Value == "(":
            {
                if (IsOperator(")")) { Next(); return new TupleExpr([]); }
                var first = ParseExpression();
                if (IsOperator(")")) { Next(); return first; }
                var items = new List<Expr> { first };
                while (IsOperator(","))
                {
                    Next();
                    if (IsOperator(")")) break;
                    items.Add(ParseExpression());
                }
                ExpectOperator(")");
                return new TupleExpr(items);
            }
            case TokenKind.Operator when token.Value == "[":
            {
                var items = new List<Expr>();
                while (!IsOperator("]"))
                {
                    items.Add(ParseExpression());
                    if (!IsOperator(",")) break;
                    Next();
                }
                ExpectOperator("]");
                return new ListExpr(items);
            }
            case TokenKind.Operator when token.Value == "{":
            {
                var items = new List<(Expr, Expr)>();
                while (!IsOperator("}"))
                {
                    var key = ParseExpression();
                    ExpectOperator(":");
                    items.Add((key, ParseExpression()));
                    if (!IsOperator(",")) break;
                    Next();
                }
                ExpectOperator("}");
                return new DictExpr(items);
            }
            default:
                _index--;
                throw Error("Unexpected token");
        }
    }

    private bool IsOperator(string value) => Current.Kind == TokenKind.Operator && Current.Value == value;
    private bool IsName(string value) => Current.Kind == TokenKind.Name && Current.Value == value;

    private void Expect(TokenKind kind)
    {
        if (Current.Kind != kind) throw Error($"Expected {kind}");
        Next();
    }

    private void ExpectOperator(string value)
    {
        if (!IsOperator(value)) throw Error($"Expected '{value}'");
        Next();
    }

    private string ExpectName()
    {
        if (Current.Kind != TokenKind.Name) throw Error("Expected a name");
        return Next().Value;
    }
}
