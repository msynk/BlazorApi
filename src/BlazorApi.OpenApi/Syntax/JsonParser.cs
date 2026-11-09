using System.Globalization;
using System.Text;
using BlazorApi.OpenApi.Diagnostics;

namespace BlazorApi.OpenApi.Syntax;

/// <summary>A strict RFC 8259 JSON parser that records source positions and preserves key order.</summary>
internal sealed class JsonParser
{
    private readonly string _text;
    private readonly string? _source;
    private readonly ParserLimits _limits;
    private readonly DiagnosticBag _diagnostics;
    private readonly StringBuilder _buffer = new();
    private int _pos;
    private int _line = 1;
    private int _lineStart;
    private int _depth;

    private JsonParser(string text, string? source, ParserLimits limits, DiagnosticBag diagnostics)
    {
        _text = text;
        _source = source;
        _limits = limits;
        _diagnostics = diagnostics;
    }

    public static DocNode Parse(string text, string? source, ParserLimits limits, DiagnosticBag diagnostics)
    {
        var parser = new JsonParser(text, source, limits, diagnostics);
        if (parser._pos < text.Length && text[parser._pos] == (char)0xFEFF)
        {
            parser._pos++;
            parser._lineStart = 1;
        }

        parser.SkipWhitespace();
        if (parser._pos >= text.Length)
        {
            throw parser.Error("The document is empty");
        }

        var root = parser.ParseValue();
        parser.SkipWhitespace();
        if (parser._pos < text.Length)
        {
            throw parser.Error($"Unexpected '{Describe(text[parser._pos])}' after the end of the document");
        }

        return root;
    }

    private SourceLocation Here => new(_source, _line, _pos - _lineStart + 1);

    private DocNode ParseValue()
    {
        if (_pos >= _text.Length)
        {
            throw Error("Unexpected end of document; a value was expected");
        }

        var c = _text[_pos];
        switch (c)
        {
            case '{':
                return ParseObject();
            case '[':
                return ParseArray();
            case '"':
                var location = Here;
                return new ScalarNode(location, NodeKind.String, ParseString());
            case 't':
                return ParseLiteral("true", NodeKind.Boolean);
            case 'f':
                return ParseLiteral("false", NodeKind.Boolean);
            case 'n':
                return ParseLiteral("null", NodeKind.Null);
            default:
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    return ParseNumber();
                }

                throw Error($"Unexpected '{Describe(c)}'; a value was expected");
        }
    }

    private ObjectNode ParseObject()
    {
        var node = new ObjectNode(Here);
        Enter();
        _pos++; // {
        SkipWhitespace();
        if (Peek() == '}')
        {
            _pos++;
            _depth--;
            return node;
        }

        while (true)
        {
            SkipWhitespace();
            if (Peek() != '"')
            {
                throw Error(Peek() == '}' ? "Trailing comma before '}'" : "A property name in double quotes was expected");
            }

            var keyLocation = Here;
            var key = ParseString();
            SkipWhitespace();
            if (Peek() != ':')
            {
                throw Error($"':' was expected after property name \"{key}\"");
            }

            _pos++;
            SkipWhitespace();
            var value = ParseValue();
            if (!node.Add(key, keyLocation, value))
            {
                _diagnostics.Report(DiagnosticDescriptors.DuplicateKey, keyLocation, null, key);
            }

            SkipWhitespace();
            var c = Peek();
            if (c == ',')
            {
                _pos++;
                continue;
            }

            if (c == '}')
            {
                _pos++;
                break;
            }

            throw Error(c == '\0' ? "Unexpected end of document; '}' was expected" : $"',' or '}}' was expected but found '{Describe(c)}'");
        }

        _depth--;
        return node;
    }

    private ArrayNode ParseArray()
    {
        var node = new ArrayNode(Here);
        Enter();
        _pos++; // [
        SkipWhitespace();
        if (Peek() == ']')
        {
            _pos++;
            _depth--;
            return node;
        }

        while (true)
        {
            SkipWhitespace();
            if (Peek() == ']')
            {
                throw Error("Trailing comma before ']'");
            }

            node.Add(ParseValue());
            SkipWhitespace();
            var c = Peek();
            if (c == ',')
            {
                _pos++;
                continue;
            }

            if (c == ']')
            {
                _pos++;
                break;
            }

            throw Error(c == '\0' ? "Unexpected end of document; ']' was expected" : $"',' or ']' was expected but found '{Describe(c)}'");
        }

        _depth--;
        return node;
    }

    private string ParseString()
    {
        _pos++; // opening quote
        var start = _pos;

        // Fast path: no escapes.
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == '"')
            {
                var result = _text.Substring(start, _pos - start);
                _pos++;
                return result;
            }

            if (c == '\\')
            {
                break;
            }

            if (c < 0x20)
            {
                throw Error(c == '\n' ? "Unterminated string" : "Control characters must be escaped in strings");
            }

            _pos++;
        }

        _buffer.Clear();
        _buffer.Append(_text, start, _pos - start);
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == '"')
            {
                _pos++;
                return _buffer.ToString();
            }

            if (c < 0x20)
            {
                throw Error(c == '\n' ? "Unterminated string" : "Control characters must be escaped in strings");
            }

            if (c != '\\')
            {
                _buffer.Append(c);
                _pos++;
                continue;
            }

            _pos++;
            if (_pos >= _text.Length)
            {
                break;
            }

            var e = _text[_pos++];
            switch (e)
            {
                case '"': _buffer.Append('"'); break;
                case '\\': _buffer.Append('\\'); break;
                case '/': _buffer.Append('/'); break;
                case 'b': _buffer.Append('\b'); break;
                case 'f': _buffer.Append('\f'); break;
                case 'n': _buffer.Append('\n'); break;
                case 'r': _buffer.Append('\r'); break;
                case 't': _buffer.Append('\t'); break;
                case 'u': _buffer.Append(ParseUnicodeEscape()); break;
                default:
                    _pos--;
                    throw Error($"Invalid escape sequence '\\{Describe(e)}'");
            }
        }

        throw Error("Unterminated string");
    }

    private char ParseUnicodeEscape()
    {
        if (_pos + 4 > _text.Length)
        {
            throw Error("Incomplete \\u escape sequence");
        }

        var value = 0;
        for (var i = 0; i < 4; i++)
        {
            var h = HexValue(_text[_pos + i]);
            if (h < 0)
            {
                throw Error("Invalid \\u escape sequence");
            }

            value = (value * 16) + h;
        }

        _pos += 4;
        return (char)value;
    }

    private ScalarNode ParseNumber()
    {
        var location = Here;
        var start = _pos;
        if (Peek() == '-')
        {
            _pos++;
        }

        if (Peek() == '0')
        {
            _pos++;
            if (IsDigit(Peek()))
            {
                throw Error("Leading zeros are not allowed in numbers");
            }
        }
        else if (IsDigit(Peek()))
        {
            while (IsDigit(Peek()))
            {
                _pos++;
            }
        }
        else
        {
            throw Error("Invalid number");
        }

        if (Peek() == '.')
        {
            _pos++;
            if (!IsDigit(Peek()))
            {
                throw Error("A digit was expected after the decimal point");
            }

            while (IsDigit(Peek()))
            {
                _pos++;
            }
        }

        if (Peek() is 'e' or 'E')
        {
            _pos++;
            if (Peek() is '+' or '-')
            {
                _pos++;
            }

            if (!IsDigit(Peek()))
            {
                throw Error("A digit was expected in the exponent");
            }

            while (IsDigit(Peek()))
            {
                _pos++;
            }
        }

        return new ScalarNode(location, NodeKind.Number, _text.Substring(start, _pos - start));
    }

    private ScalarNode ParseLiteral(string literal, NodeKind kind)
    {
        var location = Here;
        if (string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
        {
            throw Error($"Unexpected '{Describe(_text[_pos])}'; a value was expected");
        }

        _pos += literal.Length;
        return new ScalarNode(location, kind, literal);
    }

    private void SkipWhitespace()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == '\n')
            {
                _pos++;
                _line++;
                _lineStart = _pos;
            }
            else if (c is ' ' or '\t' or '\r')
            {
                _pos++;
            }
            else
            {
                return;
            }
        }
    }

    private void Enter()
    {
        if (++_depth > _limits.MaxDepth)
        {
            throw new SyntaxException($"The document is nested deeper than the limit of {_limits.MaxDepth}", _line, _pos - _lineStart + 1, SyntaxErrorKind.Limit);
        }
    }

    private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

    private SyntaxException Error(string message) => new(message, _line, _pos - _lineStart + 1);

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    internal static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    internal static string Describe(char c) =>
        c < 0x20 || c == 0x7F ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString();
}
