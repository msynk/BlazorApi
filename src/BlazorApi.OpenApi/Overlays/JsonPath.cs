using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Overlays;

/// <summary>A node selected by a JSONPath query, with its position in its parent.</summary>
public readonly struct JsonPathNode
{
    internal JsonPathNode(DocNode node, DocNode? parent, string? name, int index)
    {
        Node = node;
        Parent = parent;
        Name = name;
        Index = index;
    }

    /// <summary>The node.</summary>
    public DocNode Node { get; }

    /// <summary>The object or array containing the node; <see langword="null"/> for the root.</summary>
    public DocNode? Parent { get; }

    /// <summary>The member name when the parent is an object.</summary>
    public string? Name { get; }

    /// <summary>The index when the parent is an array; -1 otherwise.</summary>
    public int Index { get; }
}

/// <summary>Thrown for a JSONPath expression that is not well-formed or not valid (RFC 9535).</summary>
public sealed class JsonPathException : Exception
{
    /// <summary>Creates the exception.</summary>
    public JsonPathException(string message, int position)
        : base(message) => Position = position;

    /// <summary>The character offset of the problem.</summary>
    public int Position { get; }
}

/// <summary>
/// A JSONPath query (RFC 9535): child and descendant segments, name, wildcard, index, slice and filter selectors,
/// filter expressions with comparisons, logical operators, existence tests and the standard functions
/// <c>length</c>, <c>count</c>, <c>match</c>, <c>search</c> and <c>value</c>. Used by OpenAPI overlays.
/// </summary>
public sealed class JsonPath
{
    private const long MaxSafeInteger = 9007199254740991;
    private readonly List<Segment> _segments;

    private JsonPath(string text, List<Segment> segments)
    {
        Text = text;
        _segments = segments;
    }

    /// <summary>The query text.</summary>
    public string Text { get; }

    /// <summary>Parses a query.</summary>
    /// <exception cref="JsonPathException">The query is not well-formed or not valid.</exception>
    public static JsonPath Parse(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var parser = new Parser(text);
        var segments = parser.ParseQuery();
        return new JsonPath(text, segments);
    }

    /// <summary>Parses a query, or returns false with the reason.</summary>
    public static bool TryParse(string text, out JsonPath? path, out string? error)
    {
        try
        {
            path = Parse(text);
            error = null;
            return true;
        }
        catch (JsonPathException ex)
        {
            path = null;
            error = $"{ex.Message} (at position {ex.Position})";
            return false;
        }
    }

    /// <summary>Selects the nodes of a document, in document order.</summary>
    public IReadOnlyList<JsonPathNode> Select(DocNode root)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        return Evaluate(_segments, new JsonPathNode(root, null, null, -1), root);
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    private static List<JsonPathNode> Evaluate(List<Segment> segments, JsonPathNode start, DocNode root)
    {
        var current = new List<JsonPathNode> { start };
        foreach (var segment in segments)
        {
            var next = new List<JsonPathNode>();
            foreach (var node in current)
            {
                if (segment.Descendant)
                {
                    foreach (var descendant in DescendantsAndSelf(node))
                    {
                        ApplySelectors(segment.Selectors, descendant, root, next);
                    }
                }
                else
                {
                    ApplySelectors(segment.Selectors, node, root, next);
                }
            }

            current = next;
        }

        return current;
    }

    private static IEnumerable<JsonPathNode> DescendantsAndSelf(JsonPathNode node)
    {
        yield return node;
        foreach (var child in Children(node))
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<JsonPathNode> Children(JsonPathNode node)
    {
        switch (node.Node)
        {
            case ObjectNode obj:
                foreach (var property in obj.Properties)
                {
                    yield return new JsonPathNode(property.Value, obj, property.Name, -1);
                }

                break;
            case ArrayNode array:
                for (var i = 0; i < array.Count; i++)
                {
                    yield return new JsonPathNode(array[i], array, null, i);
                }

                break;
        }
    }

    private static void ApplySelectors(List<Selector> selectors, JsonPathNode node, DocNode root, List<JsonPathNode> output)
    {
        foreach (var selector in selectors)
        {
            selector.Select(node, root, output);
        }
    }

    // ------------------------------------------------------------------ query model

    private sealed class Segment
    {
        public Segment(bool descendant, List<Selector> selectors)
        {
            Descendant = descendant;
            Selectors = selectors;
        }

        public bool Descendant { get; }

        public List<Selector> Selectors { get; }

        /// <summary>Whether the segment selects at most one node (a child segment with one name or index selector).</summary>
        public bool IsSingular => !Descendant && Selectors.Count == 1 && Selectors[0] is NameSelector or IndexSelector;
    }

    private abstract class Selector
    {
        public abstract void Select(JsonPathNode node, DocNode root, List<JsonPathNode> output);
    }

    private sealed class NameSelector(string name) : Selector
    {
        public override void Select(JsonPathNode node, DocNode root, List<JsonPathNode> output)
        {
            if (node.Node is ObjectNode obj && obj.TryGetValue(name, out var value))
            {
                output.Add(new JsonPathNode(value, obj, name, -1));
            }
        }
    }

    private sealed class WildcardSelector : Selector
    {
        public override void Select(JsonPathNode node, DocNode root, List<JsonPathNode> output) => output.AddRange(Children(node));
    }

    private sealed class IndexSelector(long index) : Selector
    {
        public override void Select(JsonPathNode node, DocNode root, List<JsonPathNode> output)
        {
            if (node.Node is ArrayNode array)
            {
                var i = index < 0 ? array.Count + index : index;
                if (i >= 0 && i < array.Count)
                {
                    output.Add(new JsonPathNode(array[(int)i], array, null, (int)i));
                }
            }
        }
    }

    private sealed class SliceSelector(long? start, long? end, long step) : Selector
    {
        public override void Select(JsonPathNode node, DocNode root, List<JsonPathNode> output)
        {
            if (node.Node is not ArrayNode array || step == 0)
            {
                return;
            }

            // RFC 9535, 2.3.4.2.2.
            long length = array.Count;
            static long Normalize(long i, long len) => i >= 0 ? i : len + i;
            if (step > 0)
            {
                var lower = Math.Min(Math.Max(Normalize(start ?? 0, length), 0), length);
                var upper = Math.Min(Math.Max(Normalize(end ?? length, length), 0), length);
                for (var i = lower; i < upper; i += step)
                {
                    output.Add(new JsonPathNode(array[(int)i], array, null, (int)i));
                }
            }
            else
            {
                var upper = Math.Min(Math.Max(Normalize(start ?? length - 1, length), -1), length - 1);
                var lower = Math.Min(Math.Max(end is { } e ? Normalize(e, length) : -length - 1, -1), length - 1);
                for (var i = upper; lower < i; i += step)
                {
                    output.Add(new JsonPathNode(array[(int)i], array, null, (int)i));
                }
            }
        }
    }

    private sealed class FilterSelector(Expression filter) : Selector
    {
        public override void Select(JsonPathNode node, DocNode root, List<JsonPathNode> output)
        {
            foreach (var child in Children(node))
            {
                if (filter.Test(child.Node, root))
                {
                    output.Add(child);
                }
            }
        }
    }

    // ------------------------------------------------------------------ filter expressions

    /// <summary>The result of a comparable or function: a JSON value or Nothing.</summary>
    private readonly struct Value(DocNode? node)
    {
        public DocNode? Node { get; } = node;

        public bool IsNothing => Node is null;
    }

    private abstract class Expression
    {
        public abstract bool Test(DocNode current, DocNode root);
    }

    private sealed class OrExpression(List<Expression> operands) : Expression
    {
        public override bool Test(DocNode current, DocNode root) => operands.Any(o => o.Test(current, root));
    }

    private sealed class AndExpression(List<Expression> operands) : Expression
    {
        public override bool Test(DocNode current, DocNode root) => operands.All(o => o.Test(current, root));
    }

    private sealed class NotExpression(Expression operand) : Expression
    {
        public override bool Test(DocNode current, DocNode root) => !operand.Test(current, root);
    }

    private sealed class ExistenceExpression(QueryOperand query) : Expression
    {
        public override bool Test(DocNode current, DocNode root) => query.Nodes(current, root).Count > 0;
    }

    private sealed class LogicalFunctionExpression(FunctionCall call) : Expression
    {
        public override bool Test(DocNode current, DocNode root) => call.Logical(current, root);
    }

    private sealed class ComparisonExpression(Operand left, string op, Operand right) : Expression
    {
        public override bool Test(DocNode current, DocNode root)
        {
            var l = left.Evaluate(current, root);
            var r = right.Evaluate(current, root);
            return op switch
            {
                "==" => AreEqual(l, r),
                "!=" => !AreEqual(l, r),
                "<" => IsLess(l, r),
                "<=" => IsLess(l, r) || AreEqual(l, r),
                ">" => IsLess(r, l),
                _ => IsLess(r, l) || AreEqual(l, r),
            };
        }

        private static bool AreEqual(Value left, Value right)
        {
            if (left.IsNothing || right.IsNothing)
            {
                return left.IsNothing && right.IsNothing;
            }

            if (left.Node is ScalarNode { Kind: NodeKind.Number } ln && right.Node is ScalarNode { Kind: NodeKind.Number } rn)
            {
                return CompareNumbers(ln, rn) == 0;
            }

            return DocNode.DeepEquals(left.Node, right.Node);
        }

        private static bool IsLess(Value left, Value right)
        {
            if (left.Node is ScalarNode { Kind: NodeKind.Number } ln && right.Node is ScalarNode { Kind: NodeKind.Number } rn)
            {
                return CompareNumbers(ln, rn) < 0;
            }

            if (left.Node is ScalarNode { Kind: NodeKind.String } ls && right.Node is ScalarNode { Kind: NodeKind.String } rs)
            {
                return CompareScalarValues(ls.Value, rs.Value) < 0;
            }

            return false;
        }

        private static int CompareNumbers(ScalarNode left, ScalarNode right)
        {
            if (left.TryGetDecimal(out var ld) && right.TryGetDecimal(out var rd))
            {
                return ld.CompareTo(rd);
            }

            left.TryGetDouble(out var l);
            right.TryGetDouble(out var r);
            return l.CompareTo(r);
        }

        /// <summary>Compares strings by Unicode scalar values (not UTF-16 code units).</summary>
        private static int CompareScalarValues(string left, string right)
        {
            var i = 0;
            var j = 0;
            while (i < left.Length && j < right.Length)
            {
                var a = char.ConvertToUtf32(left, i);
                var b = char.ConvertToUtf32(right, j);
                if (a != b)
                {
                    return a.CompareTo(b);
                }

                i += char.IsSurrogatePair(left, i) ? 2 : 1;
                j += char.IsSurrogatePair(right, j) ? 2 : 1;
            }

            return (left.Length - i).CompareTo(right.Length - j);
        }
    }

    private abstract class Operand
    {
        public abstract Value Evaluate(DocNode current, DocNode root);
    }

    private sealed class LiteralOperand(DocNode literal) : Operand
    {
        public override Value Evaluate(DocNode current, DocNode root) => new(literal);
    }

    private sealed class QueryOperand(bool relative, List<Segment> segments) : Operand
    {
        public bool IsSingular => segments.All(s => s.IsSingular);

        public List<JsonPathNode> Nodes(DocNode current, DocNode root)
        {
            var start = relative ? current : root;
            return JsonPath.Evaluate(segments, new JsonPathNode(start, null, null, -1), root);
        }

        public override Value Evaluate(DocNode current, DocNode root)
        {
            var nodes = Nodes(current, root);
            return nodes.Count == 1 ? new Value(nodes[0].Node) : default;
        }
    }

    private sealed class FunctionCall : Operand
    {
        private readonly string _name;
        private readonly List<object> _arguments;

        public FunctionCall(string name, List<object> arguments)
        {
            _name = name;
            _arguments = arguments;
        }

        public string Name => _name;

        public override Value Evaluate(DocNode current, DocNode root)
        {
            switch (_name)
            {
                case "length":
                    var value = ArgumentValue(0, current, root);
                    var length = value.Node switch
                    {
                        ScalarNode { Kind: NodeKind.String } s => CodePoints(s.Value),
                        ArrayNode a => a.Count,
                        ObjectNode o => o.Count,
                        _ => -1,
                    };
                    return length < 0 ? default : new Value(Number(length));
                case "count":
                    return new Value(Number(((QueryOperand)_arguments[0]).Nodes(current, root).Count));
                case "value":
                    var nodes = ((QueryOperand)_arguments[0]).Nodes(current, root);
                    return nodes.Count == 1 ? new Value(nodes[0].Node) : default;
                default:
                    return default;
            }
        }

        public bool Logical(DocNode current, DocNode root)
        {
            if (_name is not ("match" or "search"))
            {
                return false;
            }

            if (ArgumentValue(0, current, root).Node is not ScalarNode { Kind: NodeKind.String } input ||
                ArgumentValue(1, current, root).Node is not ScalarNode { Kind: NodeKind.String } pattern ||
                ToRegex(pattern.Value, anchored: _name == "match") is not { } regex)
            {
                return false;
            }

            try
            {
                return regex.IsMatch(input.Value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private Value ArgumentValue(int index, DocNode current, DocNode root) => _arguments[index] switch
        {
            Operand operand => operand.Evaluate(current, root),
            _ => default,
        };

        private static int CodePoints(string text)
        {
            var count = 0;
            for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// Converts an I-Regexp (RFC 9485) to a .NET regular expression: <c>.</c> matches any character except line
        /// breaks, and <c>match</c> is anchored to the whole string. Invalid expressions yield <see langword="null"/>.
        /// </summary>
        private static Regex? ToRegex(string pattern, bool anchored)
        {
            var builder = new StringBuilder();
            var inClass = false;
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    builder.Append(c).Append(pattern[++i]);
                    continue;
                }

                if (inClass)
                {
                    inClass = c != ']';
                    builder.Append(c);
                }
                else if (c == '[')
                {
                    inClass = true;
                    builder.Append(c);
                }
                else if (c == '.')
                {
                    builder.Append("[^\\n\\r]");
                }
                else
                {
                    builder.Append(c);
                }
            }

            var text = anchored ? "\\A(?:" + builder + ")\\z" : builder.ToString();
            try
            {
                return new Regex(text, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    private static ScalarNode Number(long value) =>
        new(SourceLocation.None, NodeKind.Number, value.ToString(CultureInfo.InvariantCulture));

    // ------------------------------------------------------------------ parser

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;

        public Parser(string text) => _text = text;

        public List<Segment> ParseQuery()
        {
            Expect('$');
            var segments = ParseSegments();
            if (_position < _text.Length)
            {
                throw Error($"Unexpected '{_text[_position]}'");
            }

            return segments;
        }

        private List<Segment> ParseSegments()
        {
            var segments = new List<Segment>();
            while (true)
            {
                var save = _position;
                SkipWhitespace();
                if (Peek('.') || Peek('['))
                {
                    segments.Add(ParseSegment());
                }
                else
                {
                    _position = save;
                    return segments;
                }
            }
        }

        private Segment ParseSegment()
        {
            if (TryConsume(".."))
            {
                if (Peek('['))
                {
                    return new Segment(true, ParseBracketed());
                }

                if (TryConsume("*"))
                {
                    return new Segment(true, [new WildcardSelector()]);
                }

                return new Segment(true, [new NameSelector(ParseMemberName())]);
            }

            if (TryConsume("."))
            {
                if (TryConsume("*"))
                {
                    return new Segment(false, [new WildcardSelector()]);
                }

                return new Segment(false, [new NameSelector(ParseMemberName())]);
            }

            return new Segment(false, ParseBracketed());
        }

        private string ParseMemberName()
        {
            var start = _position;
            while (_position < _text.Length && IsNameChar(_text[_position], _position == start))
            {
                _position++;
            }

            if (_position == start)
            {
                throw Error("Expected a member name, '*' or '['");
            }

            return _text.Substring(start, _position - start);
        }

        private static bool IsNameChar(char c, bool first) =>
            c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_' || c >= 0x80 || (!first && c is >= '0' and <= '9');

        private List<Selector> ParseBracketed()
        {
            Expect('[');
            var selectors = new List<Selector>();
            while (true)
            {
                SkipWhitespace();
                selectors.Add(ParseSelector());
                SkipWhitespace();
                if (TryConsume(","))
                {
                    continue;
                }

                Expect(']');
                return selectors;
            }
        }

        private Selector ParseSelector()
        {
            if (Peek('\'') || Peek('"'))
            {
                return new NameSelector(ParseString());
            }

            if (TryConsume("*"))
            {
                return new WildcardSelector();
            }

            if (TryConsume("?"))
            {
                SkipWhitespace();
                return new FilterSelector(ParseLogicalOr());
            }

            // index or slice
            long? start = PeekInteger() ? ParseInteger() : null;
            SkipWhitespace();
            if (!TryConsume(":"))
            {
                return start is { } index ? new IndexSelector(index) : throw Error("Expected a selector");
            }

            SkipWhitespace();
            long? end = PeekInteger() ? ParseInteger() : null;
            SkipWhitespace();
            long step = 1;
            if (TryConsume(":"))
            {
                SkipWhitespace();
                if (PeekInteger())
                {
                    step = ParseInteger();
                }
            }

            return new SliceSelector(start, end, step);
        }

        private bool PeekInteger() =>
            _position < _text.Length && (_text[_position] is >= '0' and <= '9' || _text[_position] == '-');

        private long ParseInteger()
        {
            var start = _position;
            TryConsume("-");
            var digits = _position;
            while (_position < _text.Length && _text[_position] is >= '0' and <= '9')
            {
                _position++;
            }

            var text = _text.Substring(start, _position - start);
            if (_position == digits || (_text[digits] == '0' && (_position - digits > 1 || start != digits)))
            {
                throw Error($"'{text}' is not a valid integer", start);
            }

            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value > MaxSafeInteger || value < -MaxSafeInteger)
            {
                throw Error($"'{text}' is out of range", start);
            }

            return value;
        }

        private string ParseString()
        {
            var quote = _text[_position++];
            var builder = new StringBuilder();
            while (true)
            {
                if (_position >= _text.Length)
                {
                    throw Error("Unterminated string");
                }

                var c = _text[_position++];
                if (c == quote)
                {
                    return builder.ToString();
                }

                if (c < 0x20)
                {
                    throw Error("Control characters must be escaped in strings", _position - 1);
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (_position >= _text.Length)
                {
                    throw Error("Unterminated escape");
                }

                var e = _text[_position++];
                switch (e)
                {
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case '/': builder.Append('/'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'u': builder.Append(ParseUnicodeEscape()); break;
                    default:
                        if (e == quote)
                        {
                            builder.Append(e);
                            break;
                        }

                        throw Error($"Invalid escape '\\{e}'", _position - 2);
                }
            }
        }

        private char ParseUnicodeEscape()
        {
            if (_position + 4 > _text.Length ||
                !int.TryParse(_text.Substring(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                throw Error("Invalid \\u escape");
            }

            _position += 4;
            return (char)code;
        }

        // logical-or-expr = logical-and-expr *(S "||" S logical-and-expr)
        private Expression ParseLogicalOr()
        {
            var operands = new List<Expression> { ParseLogicalAnd() };
            while (true)
            {
                SkipWhitespace();
                if (!TryConsume("||"))
                {
                    return operands.Count == 1 ? operands[0] : new OrExpression(operands);
                }

                SkipWhitespace();
                operands.Add(ParseLogicalAnd());
            }
        }

        private Expression ParseLogicalAnd()
        {
            var operands = new List<Expression> { ParseBasic() };
            while (true)
            {
                var save = _position;
                SkipWhitespace();
                if (!TryConsume("&&"))
                {
                    _position = save;
                    return operands.Count == 1 ? operands[0] : new AndExpression(operands);
                }

                SkipWhitespace();
                operands.Add(ParseBasic());
            }
        }

        private Expression ParseBasic()
        {
            var start = _position;
            if (TryConsume("!"))
            {
                SkipWhitespace();
                if (TryConsume("("))
                {
                    var inner = ParseParenthesized();
                    return new NotExpression(inner);
                }

                var test = ParseTestOperand(start);
                return new NotExpression(test);
            }

            if (TryConsume("("))
            {
                return ParseParenthesized();
            }

            var left = ParseComparable(out var testExpression);
            var save = _position;
            SkipWhitespace();
            var op = ParseComparisonOperator();
            if (op is null)
            {
                _position = save;
                return testExpression ?? throw Error("Expected a comparison or a test", start);
            }

            if (left is QueryOperand { IsSingular: false })
            {
                throw Error("Only singular queries can be compared", start);
            }

            if (left is FunctionCall { Name: "match" or "search" })
            {
                throw Error("A logical function cannot be compared", start);
            }

            SkipWhitespace();
            var rightStart = _position;
            var right = ParseComparable(out _);
            if (right is QueryOperand { IsSingular: false })
            {
                throw Error("Only singular queries can be compared", rightStart);
            }

            if (right is FunctionCall { Name: "match" or "search" })
            {
                throw Error("A logical function cannot be compared", rightStart);
            }

            return new ComparisonExpression(left, op, right);
        }

        private Expression ParseParenthesized()
        {
            SkipWhitespace();
            var inner = ParseLogicalOr();
            SkipWhitespace();
            Expect(')');
            return inner;
        }

        private Expression ParseTestOperand(int start)
        {
            ParseComparable(out var test);
            return test ?? throw Error("Expected a query or a logical function after '!'", start);
        }

        private string? ParseComparisonOperator()
        {
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
            {
                if (TryConsume(op))
                {
                    return op;
                }
            }

            return null;
        }

        /// <summary>A literal, a query or a function call; queries and logical functions can also stand alone as tests.</summary>
        private Operand ParseComparable(out Expression? test)
        {
            test = null;
            if (Peek('@') || Peek('$'))
            {
                var relative = _text[_position++] == '@';
                var query = new QueryOperand(relative, ParseSegments());
                test = new ExistenceExpression(query);
                return query;
            }

            if (Peek('\'') || Peek('"'))
            {
                return new LiteralOperand(new ScalarNode(SourceLocation.None, NodeKind.String, ParseString()));
            }

            if (Peek('-') || (_position < _text.Length && _text[_position] is >= '0' and <= '9'))
            {
                return new LiteralOperand(ParseNumberLiteral());
            }

            foreach (var (word, kind) in new[] { ("true", NodeKind.Boolean), ("false", NodeKind.Boolean), ("null", NodeKind.Null) })
            {
                if (TryConsumeWord(word))
                {
                    return new LiteralOperand(new ScalarNode(SourceLocation.None, kind, word));
                }
            }

            var call = ParseFunctionCall();
            if (call.Name is "match" or "search")
            {
                test = new LogicalFunctionExpression(call);
            }

            return call;
        }

        private ScalarNode ParseNumberLiteral()
        {
            var start = _position;
            TryConsume("-");
            while (_position < _text.Length && (_text[_position] is >= '0' and <= '9' or '.' or 'e' or 'E' or '+' or '-'))
            {
                _position++;
            }

            var text = _text.Substring(start, _position - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || text.EndsWith(".", StringComparison.Ordinal) ||
                text.StartsWith(".", StringComparison.Ordinal) || text.StartsWith("-.", StringComparison.Ordinal))
            {
                throw Error($"'{text}' is not a valid number", start);
            }

            return new ScalarNode(SourceLocation.None, NodeKind.Number, text);
        }

        private FunctionCall ParseFunctionCall()
        {
            var start = _position;
            while (_position < _text.Length && (_text[_position] is >= 'a' and <= 'z' or '_' || (_position > start && _text[_position] is >= '0' and <= '9')))
            {
                _position++;
            }

            var name = _text.Substring(start, _position - start);
            if (name.Length == 0 || !Peek('('))
            {
                throw Error("Expected a literal, a query or a function call", start);
            }

            var arity = name switch
            {
                "length" or "count" or "value" => 1,
                "match" or "search" => 2,
                _ => throw Error($"Unknown function '{name}'", start),
            };
            _position++;
            var arguments = new List<object>();
            SkipWhitespace();
            while (!Peek(')'))
            {
                var argumentStart = _position;
                var argument = ParseComparable(out _);
                var needsNodes = name is "count" or "value";
                if (needsNodes && argument is not QueryOperand)
                {
                    throw Error($"The argument of {name}() must be a query", argumentStart);
                }

                if (!needsNodes && argument is QueryOperand { IsSingular: false })
                {
                    throw Error($"The arguments of {name}() must be singular queries or values", argumentStart);
                }

                arguments.Add(argument);
                SkipWhitespace();
                if (!TryConsume(","))
                {
                    break;
                }

                SkipWhitespace();
            }

            Expect(')');
            if (arguments.Count != arity)
            {
                throw Error($"{name}() takes {arity} argument(s)", start);
            }

            return new FunctionCall(name, arguments);
        }

        private bool TryConsumeWord(string word)
        {
            if (string.CompareOrdinal(_text, _position, word, 0, word.Length) == 0 &&
                (_position + word.Length >= _text.Length || !IsNameChar(_text[_position + word.Length], first: false)))
            {
                _position += word.Length;
                return true;
            }

            return false;
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && _text[_position] is ' ' or '\t' or '\n' or '\r')
            {
                _position++;
            }
        }

        private bool Peek(char c) => _position < _text.Length && _text[_position] == c;

        private bool TryConsume(string token)
        {
            if (string.CompareOrdinal(_text, _position, token, 0, token.Length) == 0)
            {
                _position += token.Length;
                return true;
            }

            return false;
        }

        private void Expect(char c)
        {
            if (!Peek(c))
            {
                throw Error(_position < _text.Length ? $"Expected '{c}' but found '{_text[_position]}'" : $"Expected '{c}'");
            }

            _position++;
        }

        private JsonPathException Error(string message, int? position = null) => new(message, position ?? _position);
    }
}
