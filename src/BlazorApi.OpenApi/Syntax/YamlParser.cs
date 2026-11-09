using System.Globalization;
using System.Text;
using BlazorApi.OpenApi.Diagnostics;

namespace BlazorApi.OpenApi.Syntax;

/// <summary>
/// A YAML 1.2 parser for the subset used by API descriptions: block and flow collections, all scalar styles
/// (plain, single- and double-quoted, literal and folded block scalars with chomping and indentation indicators),
/// comments, anchors and aliases, merge keys (<c>&lt;&lt;</c>), core-schema tags and scalar resolution. It rejects
/// complex (non-scalar or explicit <c>?</c>) keys and multi-document streams with a clear diagnostic.
/// </summary>
/// <remarks>
/// Position conventions: block collections, block scalars and <see cref="ParseBlockNodeAt"/> return positioned at
/// the start of a line (their trailing line break is consumed); inline scalars, flow collections and aliases return
/// positioned right after their last character, and the block caller then consumes the rest of the line.
/// </remarks>
internal sealed class YamlParser
{
    private readonly string _text;
    private readonly string? _source;
    private readonly ParserLimits _limits;
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, DocNode?> _anchors = new(StringComparer.Ordinal);
    private readonly StringBuilder _buffer = new();
    private int _pos;
    private int _line = 1;
    private int _lineStart;
    private int _depth;
    private long _aliasExpansion;

    private YamlParser(string text, string? source, ParserLimits limits, DiagnosticBag diagnostics)
    {
        _text = text;
        _source = source;
        _limits = limits;
        _diagnostics = diagnostics;
    }

    public static DocNode Parse(string text, string? source, ParserLimits limits, DiagnosticBag diagnostics)
    {
        var parser = new YamlParser(text, source, limits, diagnostics);
        return parser.ParseStream();
    }

    private int Column => _pos - _lineStart;

    private SourceLocation Here => new(_source, _line, Column + 1);

    // ------------------------------------------------------------------ document

    private DocNode ParseStream()
    {
        if (_pos < _text.Length && _text[_pos] == (char)0xFEFF)
        {
            _pos++;
            _lineStart = _pos;
        }

        DocNode root;
        var sawDirective = false;
        while (true)
        {
            var indent = PeekNextContentIndent();
            if (indent == 0 && Peek() == '%')
            {
                sawDirective = true;
                SkipToLineEnd();
                ConsumeLineBreak();
                continue;
            }

            if (indent < 0 && IsDocumentMarker('-'))
            {
                _pos += 3;
                root = ParseIndicatedValue(-1, isMappingValue: false);
                break;
            }

            if (sawDirective)
            {
                throw Error("Directives must be followed by a '---' document start marker");
            }

            if (indent < 0)
            {
                root = new ScalarNode(Here, NodeKind.Null, "null", string.Empty);
                break;
            }

            _pos += indent;
            root = ParseBlockNodeAt(indent, -1, allowKey: true, allowSequence: true);
            break;
        }

        var next = PeekNextContentIndent();
        if (next < 0 && IsDocumentMarker('.'))
        {
            _pos += 3;
            ExpectEndOfLine();
            next = PeekNextContentIndent();
        }

        if (next < 0 && IsDocumentMarker('-'))
        {
            throw new SyntaxException("Multiple YAML documents in one stream are not supported", _line, Column + 1);
        }

        if (next >= 0)
        {
            _pos += next;
            throw Error("Unexpected content; check the indentation of this line");
        }

        return root;
    }

    // ------------------------------------------------------------------ block structure

    /// <summary>
    /// Parses the value after an indicator (<c>key:</c>, <c>- </c> or <c>---</c>): inline content on the same line or a
    /// block node on the following lines.
    /// </summary>
    private DocNode ParseIndicatedValue(int parentIndent, bool isMappingValue)
    {
        SkipInlineSpace();
        if (!AtLineEndOrComment())
        {
            return ParseBlockNodeAt(Column, parentIndent, allowKey: !isMappingValue, allowSequence: !isMappingValue);
        }

        var location = Here;
        ExpectEndOfLine();
        return ParseFollowingLines(parentIndent, isMappingValue, location);
    }

    private DocNode ParseFollowingLines(int parentIndent, bool isMappingValue, SourceLocation emptyLocation)
    {
        var indent = PeekNextContentIndent();
        if (indent > parentIndent)
        {
            _pos += indent;
            return ParseBlockNodeAt(indent, parentIndent, allowKey: true, allowSequence: true);
        }

        if (indent == parentIndent && isMappingValue && IsSequenceIndicatorAt(_pos + indent))
        {
            // A block sequence may sit at the same indentation as the key that owns it.
            _pos += indent;
            return ParseBlockSequence(indent);
        }

        return new ScalarNode(emptyLocation, NodeKind.Null, "null", string.Empty);
    }

    /// <summary>Parses a block node whose first character is at the current position.</summary>
    private DocNode ParseBlockNodeAt(int indent, int parentIndent, bool allowKey, bool allowSequence)
    {
        var location = Here;
        ParseProperties(out var anchor, out var tag);
        if (anchor is not null || tag is not null)
        {
            SkipInlineSpace();
            if (AtLineEndOrComment())
            {
                ExpectEndOfLine();
                var onNextLines = ParseFollowingLines(parentIndent, isMappingValue: !allowKey, location);
                return Complete(onNextLines, anchor, tag);
            }

            location = Here;
        }

        var c = Peek();
        DocNode node;
        if (c == '-' && IsBlankOrEnd(Peek(1)))
        {
            if (!allowSequence)
            {
                throw Error("A block sequence cannot start on the same line as its mapping key");
            }

            node = ParseBlockSequence(Column);
        }
        else if (c == '?' && IsBlankOrEnd(Peek(1)))
        {
            throw Unsupported("Explicit mapping keys ('? key') are not supported");
        }
        else if (c is '|' or '>')
        {
            node = ParseBlockScalar(parentIndent, location);
        }
        else if (c == '*')
        {
            node = ParseAlias();
            SkipInlineSpace();
            if (Peek() == ':')
            {
                throw Unsupported("Aliases used as mapping keys are not supported");
            }

            ExpectEndOfLine();
        }
        else if (c is '[' or '{')
        {
            node = ParseFlowCollection();
            SkipInlineSpace();
            if (Peek() == ':')
            {
                throw Unsupported("Flow collections used as mapping keys are not supported");
            }

            ExpectEndOfLine();
        }
        else if (c is '"' or '\'')
        {
            var startLine = _line;
            var value = c == '"' ? ParseDoubleQuoted() : ParseSingleQuoted();
            SkipInlineSpace();
            if (Peek() == ':' && IsBlankOrEnd(Peek(1)))
            {
                if (!allowKey)
                {
                    throw Error("Mapping values are not allowed here; quote the whole value or move it to its own line");
                }

                if (_line != startLine)
                {
                    throw Error("An implicit mapping key must fit on one line");
                }

                if (anchor is not null || tag is not null)
                {
                    throw Unsupported("Anchors and tags on implicit mapping keys are not supported");
                }

                return ParseBlockMapping(indent, value, location);
            }

            node = new ScalarNode(location, NodeKind.String, value);
            ExpectEndOfLine();
        }
        else
        {
            if (!IsPlainStart(c, Peek(1), inFlow: false))
            {
                throw Error($"Unexpected character '{JsonParser.Describe(c)}'");
            }

            var first = ScanPlainLine(inFlow: false);
            if (Peek() == ':')
            {
                if (!allowKey)
                {
                    throw Error("Mapping values are not allowed here; quote the whole value or move it to its own line");
                }

                if (anchor is not null || tag is not null)
                {
                    throw Unsupported("Anchors and tags on implicit mapping keys are not supported");
                }

                return ParseBlockMapping(indent, first, location);
            }

            var text = Peek() == '#' ? first : ContinuePlain(first, parentIndent);
            node = tag is null ? ResolvePlain(location, text) : new ScalarNode(location, NodeKind.String, text);
            ExpectEndOfLine();
        }

        return Complete(node, anchor, tag);
    }

    private ObjectNode ParseBlockMapping(int indent, string firstKey, SourceLocation location)
    {
        var node = new ObjectNode(location);
        Enter();
        List<DocNode>? merges = null;
        var key = firstKey;
        var keyLocation = location;
        var keyIsPlain = true;
        while (true)
        {
            _pos++; // ':'
            var value = ParseIndicatedValue(indent, isMappingValue: true);
            AddEntry(node, key, keyIsPlain, keyLocation, value, ref merges);

            var next = PeekNextContentIndent();
            if (next < indent)
            {
                break;
            }

            _pos += next;
            if (next > indent)
            {
                throw Error("Unexpected indentation; this line is indented more than the mapping keys above it");
            }

            keyLocation = Here;
            var c = Peek();
            if (c == '-' && IsBlankOrEnd(Peek(1)))
            {
                throw Error("A sequence item is not allowed here; a mapping key was expected");
            }

            if (c is '"' or '\'')
            {
                key = c == '"' ? ParseDoubleQuoted() : ParseSingleQuoted();
                keyIsPlain = false;
                if (keyLocation.Line != _line)
                {
                    throw Error("An implicit mapping key must fit on one line");
                }

                SkipInlineSpace();
            }
            else if (c == '?' && IsBlankOrEnd(Peek(1)))
            {
                throw Unsupported("Explicit mapping keys ('? key') are not supported");
            }
            else if (c is '[' or '{' or '*' or '&' or '!')
            {
                throw Unsupported("Mapping keys must be plain or quoted scalars");
            }
            else
            {
                if (!IsPlainStart(c, Peek(1), inFlow: false))
                {
                    throw Error($"Unexpected character '{JsonParser.Describe(c)}'; a mapping key was expected");
                }

                key = ScanPlainLine(inFlow: false);
                keyIsPlain = true;
            }

            if (Peek() != ':' || !IsBlankOrEnd(Peek(1)))
            {
                throw Error($"':' was expected after the mapping key '{key}'");
            }
        }

        ApplyMerges(node, merges);
        _depth--;
        return node;
    }

    private ArrayNode ParseBlockSequence(int indent)
    {
        var node = new ArrayNode(Here);
        Enter();
        while (true)
        {
            _pos++; // '-'
            node.Add(ParseIndicatedValue(indent, isMappingValue: false));
            var next = PeekNextContentIndent();
            if (next == indent && IsSequenceIndicatorAt(_pos + next))
            {
                _pos += next;
                continue;
            }

            if (next > indent)
            {
                _pos += next;
                throw Error("Unexpected indentation inside a sequence item");
            }

            break;
        }

        _depth--;
        return node;
    }

    // ------------------------------------------------------------------ block scalars

    private ScalarNode ParseBlockScalar(int parentIndent, SourceLocation location)
    {
        var literal = Peek() == '|';
        _pos++;
        var chomping = 0; // -1 strip, 0 clip, +1 keep
        var explicitIndent = 0;
        for (var i = 0; i < 2; i++)
        {
            var c = Peek();
            if (c == '-' && chomping == 0)
            {
                chomping = -1;
                _pos++;
            }
            else if (c == '+' && chomping == 0)
            {
                chomping = 1;
                _pos++;
            }
            else if (c >= '1' && c <= '9' && explicitIndent == 0)
            {
                explicitIndent = c - '0';
                _pos++;
            }
        }

        if (!IsBlankOrEnd(Peek()) && Peek() != '#')
        {
            throw Error("Invalid block scalar header");
        }

        ExpectEndOfLine();

        var contentIndent = explicitIndent > 0 ? (parentIndent < 0 ? 0 : parentIndent) + explicitIndent : -1;
        var lines = new List<string>();
        var lastLineHasBreak = true;
        while (_pos < _text.Length)
        {
            var lineStart = _pos;
            var spaces = 0;
            while (_pos < _text.Length && _text[_pos] == ' ' && (contentIndent < 0 || spaces < contentIndent))
            {
                _pos++;
                spaces++;
            }

            var atBreak = _pos >= _text.Length || _text[_pos] is '\n' or '\r';
            if (atBreak)
            {
                lines.Add(string.Empty);
                lastLineHasBreak = ConsumeLineBreak();
                continue;
            }

            if (contentIndent < 0)
            {
                // Auto-detect the content indentation from the first non-empty line.
                if (spaces <= parentIndent || (spaces == 0 && parentIndent < 0 && IsDocumentMarkerAt(lineStart)))
                {
                    _pos = lineStart;
                    break;
                }

                if (_text[_pos] == '\t')
                {
                    throw Error("Tabs are not allowed for the indentation of a block scalar");
                }

                contentIndent = spaces;
            }
            else if (spaces < contentIndent)
            {
                // Less indented: either the scalar ended, or this is a whitespace-only line.
                var probe = _pos;
                while (probe < _text.Length && _text[probe] is ' ' or '\t')
                {
                    probe++;
                }

                if (probe >= _text.Length || _text[probe] is '\n' or '\r')
                {
                    _pos = probe;
                    lines.Add(string.Empty);
                    lastLineHasBreak = ConsumeLineBreak();
                    continue;
                }

                _pos = lineStart;
                break;
            }

            if (spaces == 0 && contentIndent == 0 && IsDocumentMarkerAt(lineStart))
            {
                _pos = lineStart;
                break;
            }

            var start = _pos;
            SkipToLineEnd();
            lines.Add(_text.Substring(start, _pos - start));
            lastLineHasBreak = ConsumeLineBreak();
        }

        return new ScalarNode(location, NodeKind.String, BuildBlockScalar(lines, literal, chomping, lastLineHasBreak));
    }

    private static string BuildBlockScalar(List<string> lines, bool literal, int chomping, bool lastLineHasBreak)
    {
        var lastContent = lines.Count - 1;
        while (lastContent >= 0 && lines[lastContent].Length == 0)
        {
            lastContent--;
        }

        var trailingEmpty = lines.Count - 1 - lastContent;
        var builder = new StringBuilder();
        if (lastContent >= 0)
        {
            var firstContent = 0;
            while (lines[firstContent].Length == 0)
            {
                builder.Append('\n');
                firstContent++;
            }

            builder.Append(lines[firstContent]);
            var previousMoreIndented = IsMoreIndented(lines[firstContent]);
            var empties = 0;
            for (var i = firstContent + 1; i <= lastContent; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                {
                    empties++;
                    continue;
                }

                var moreIndented = IsMoreIndented(line);
                if (literal)
                {
                    builder.Append('\n', empties + 1);
                }
                else if (previousMoreIndented || moreIndented)
                {
                    builder.Append('\n', empties + 1);
                }
                else if (empties == 0)
                {
                    builder.Append(' ');
                }
                else
                {
                    builder.Append('\n', empties);
                }

                builder.Append(line);
                previousMoreIndented = moreIndented;
                empties = 0;
            }

            if (chomping >= 0 && (lastLineHasBreak || trailingEmpty > 0))
            {
                builder.Append('\n');
            }
        }

        if (chomping > 0)
        {
            var extra = lastContent >= 0 ? trailingEmpty : lines.Count;
            if (lastContent >= 0 && !lastLineHasBreak && extra > 0)
            {
                extra--;
            }

            builder.Append('\n', Math.Max(extra, 0));
        }

        return builder.ToString();

        static bool IsMoreIndented(string line) => line.Length > 0 && line[0] is ' ' or '\t';
    }

    // ------------------------------------------------------------------ flow collections

    private DocNode ParseFlowCollection()
    {
        Enter();
        var location = Here;
        DocNode result;
        if (Peek() == '[')
        {
            var array = new ArrayNode(location);
            _pos++;
            while (true)
            {
                SkipFlowSpace();
                if (Peek() == ']')
                {
                    _pos++;
                    break;
                }

                array.Add(ParseFlowValue());
                SkipFlowSpace();
                var c = Peek();
                if (c == ':')
                {
                    throw Unsupported("Single-pair mappings inside flow sequences are not supported");
                }

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

                throw Error(c == '\0' ? "Unterminated flow sequence; ']' was expected" : $"',' or ']' was expected but found '{JsonParser.Describe(c)}'");
            }

            result = array;
        }
        else
        {
            var map = new ObjectNode(location);
            List<DocNode>? merges = null;
            _pos++;
            while (true)
            {
                SkipFlowSpace();
                var c = Peek();
                if (c == '}')
                {
                    _pos++;
                    break;
                }

                if (c == '?' && IsBlankOrEnd(Peek(1)))
                {
                    throw Unsupported("Explicit mapping keys ('? key') are not supported");
                }

                var keyLocation = Here;
                string key;
                var keyIsPlain = false;
                var quoted = c is '"' or '\'';
                if (quoted)
                {
                    key = c == '"' ? ParseDoubleQuoted() : ParseSingleQuoted();
                }
                else if (c is '[' or '{' or '*' or '&' or '!')
                {
                    throw Unsupported("Mapping keys must be plain or quoted scalars");
                }
                else
                {
                    if (!IsPlainStart(c, Peek(1), inFlow: true))
                    {
                        throw Error($"Unexpected character '{JsonParser.Describe(c)}' in a flow mapping");
                    }

                    key = ScanPlainFlow();
                    keyIsPlain = true;
                }

                SkipFlowSpace();
                DocNode value;
                if (Peek() == ':' && (quoted || IsBlankOrEnd(Peek(1)) || IsFlowIndicator(Peek(1))))
                {
                    _pos++;
                    SkipFlowSpace();
                    value = Peek() is ',' or '}' ? new ScalarNode(Here, NodeKind.Null, "null", string.Empty) : ParseFlowValue();
                }
                else
                {
                    value = new ScalarNode(Here, NodeKind.Null, "null", string.Empty);
                }

                AddEntry(map, key, keyIsPlain, keyLocation, value, ref merges);
                SkipFlowSpace();
                c = Peek();
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

                throw Error(c == '\0' ? "Unterminated flow mapping; '}' was expected" : $"',' or '}}' was expected but found '{JsonParser.Describe(c)}'");
            }

            ApplyMerges(map, merges);
            result = map;
        }

        _depth--;
        return result;
    }

    private DocNode ParseFlowValue()
    {
        var location = Here;
        ParseProperties(out var anchor, out var tag);
        if (anchor is not null || tag is not null)
        {
            SkipFlowSpace();
            location = Here;
        }

        var c = Peek();
        DocNode node;
        switch (c)
        {
            case '[':
            case '{':
                node = ParseFlowCollection();
                break;
            case '"':
                node = new ScalarNode(location, NodeKind.String, ParseDoubleQuoted());
                break;
            case '\'':
                node = new ScalarNode(location, NodeKind.String, ParseSingleQuoted());
                break;
            case '*':
                node = ParseAlias();
                break;
            case ',':
            case ']':
            case '}':
                node = new ScalarNode(location, NodeKind.Null, "null", string.Empty);
                break;
            default:
                if (!IsPlainStart(c, Peek(1), inFlow: true))
                {
                    throw Error(c == '\0' ? "Unexpected end of document inside a flow collection" : $"Unexpected character '{JsonParser.Describe(c)}' in a flow collection");
                }

                var text = ScanPlainFlow();
                node = tag is null ? ResolvePlain(location, text) : new ScalarNode(location, NodeKind.String, text);
                break;
        }

        return Complete(node, anchor, tag);
    }

    /// <summary>Scans a plain scalar inside a flow collection, folding line breaks.</summary>
    private string ScanPlainFlow()
    {
        var first = ScanPlainLine(inFlow: true);
        if (!(Peek() is '\n' or '\r'))
        {
            return first;
        }

        _buffer.Clear();
        _buffer.Append(first);
        while (Peek() is '\n' or '\r')
        {
            var save = Save();
            var breaks = 0;
            while (Peek() is '\n' or '\r')
            {
                ConsumeLineBreak();
                SkipInlineSpace();
                breaks++;
            }

            var c = Peek();
            if (c == '\0' || c == '#' || IsFlowIndicator(c) || c == ':')
            {
                Restore(save);
                break;
            }

            var segment = ScanPlainLine(inFlow: true);
            if (segment.Length == 0)
            {
                Restore(save);
                break;
            }

            if (breaks == 1)
            {
                _buffer.Append(' ');
            }
            else
            {
                _buffer.Append('\n', breaks - 1);
            }

            _buffer.Append(segment);
        }

        return _buffer.ToString();
    }

    // ------------------------------------------------------------------ scalars

    /// <summary>
    /// Scans one line of a plain scalar, stopping before <c>": "</c>, <c>" #"</c>, a line break, or (in flow
    /// context) a flow indicator. Trailing whitespace is not included.
    /// </summary>
    private string ScanPlainLine(bool inFlow)
    {
        var start = _pos;
        var end = _pos;
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c is '\n' or '\r')
            {
                break;
            }

            if (c == ':')
            {
                var next = Peek(1);
                if (IsBlankOrEnd(next) || (inFlow && IsFlowIndicator(next)))
                {
                    break;
                }
            }
            else if (c == '#' && _pos > start && _text[_pos - 1] is ' ' or '\t')
            {
                break;
            }
            else if (inFlow && IsFlowIndicator(c))
            {
                break;
            }

            _pos++;
            if (c is not (' ' or '\t'))
            {
                end = _pos;
            }
        }

        return _text.Substring(start, end - start);
    }

    /// <summary>Continues a block plain scalar over more-indented lines, folding line breaks.</summary>
    private string ContinuePlain(string first, int parentIndent)
    {
        if (!(Peek() is '\n' or '\r'))
        {
            return first;
        }

        StringBuilder? builder = null;
        while (Peek() is '\n' or '\r')
        {
            var save = Save();
            var empties = 0;
            ConsumeLineBreak();
            int indent;
            while (true)
            {
                indent = 0;
                while (Peek() == ' ')
                {
                    _pos++;
                    indent++;
                }

                var probe = _pos;
                while (probe < _text.Length && _text[probe] is ' ' or '\t')
                {
                    probe++;
                }

                if (probe < _text.Length && _text[probe] is '\n' or '\r')
                {
                    _pos = probe;
                    ConsumeLineBreak();
                    empties++;
                    continue;
                }

                break;
            }

            SkipInlineSpace();
            var c = Peek();
            if (c == '\0' || indent <= parentIndent || c == '#' || (indent == 0 && IsDocumentMarkerAt(_lineStart)))
            {
                Restore(save);
                break;
            }

            var segment = ScanPlainLine(inFlow: false);
            if (Peek() == ':')
            {
                throw Error("Mapping values are not allowed inside a multi-line plain scalar; check the indentation or quote the value");
            }

            builder ??= new StringBuilder(first);
            if (empties == 0)
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append('\n', empties);
            }

            builder.Append(segment);
            if (Peek() == '#')
            {
                break;
            }
        }

        return builder?.ToString() ?? first;
    }

    private string ParseSingleQuoted()
    {
        var startLine = _line;
        var startColumn = Column + 1;
        _pos++; // '
        _buffer.Clear();
        while (true)
        {
            if (_pos >= _text.Length)
            {
                throw new SyntaxException("Unterminated single-quoted string", startLine, startColumn);
            }

            var c = _text[_pos];
            if (c == '\'')
            {
                if (Peek(1) == '\'')
                {
                    _buffer.Append('\'');
                    _pos += 2;
                    continue;
                }

                _pos++;
                return _buffer.ToString();
            }

            if (c is '\n' or '\r')
            {
                FoldQuotedLineBreak(startLine, startColumn);
                continue;
            }

            _buffer.Append(c);
            _pos++;
        }
    }

    private string ParseDoubleQuoted()
    {
        var startLine = _line;
        var startColumn = Column + 1;
        _pos++; // "
        _buffer.Clear();
        while (true)
        {
            if (_pos >= _text.Length)
            {
                throw new SyntaxException("Unterminated double-quoted string", startLine, startColumn);
            }

            var c = _text[_pos];
            if (c == '"')
            {
                _pos++;
                return _buffer.ToString();
            }

            if (c is '\n' or '\r')
            {
                FoldQuotedLineBreak(startLine, startColumn);
                continue;
            }

            if (c != '\\')
            {
                _buffer.Append(c);
                _pos++;
                continue;
            }

            _pos++;
            var e = Peek();
            _pos++;
            switch (e)
            {
                case '0': _buffer.Append('\0'); break;
                case 'a': _buffer.Append('\a'); break;
                case 'b': _buffer.Append('\b'); break;
                case 't':
                case '\t': _buffer.Append('\t'); break;
                case 'n': _buffer.Append('\n'); break;
                case 'v': _buffer.Append('\v'); break;
                case 'f': _buffer.Append('\f'); break;
                case 'r': _buffer.Append('\r'); break;
                case 'e': _buffer.Append('\u001B'); break;
                case ' ': _buffer.Append(' '); break;
                case '"': _buffer.Append('"'); break;
                case '/': _buffer.Append('/'); break;
                case '\\': _buffer.Append('\\'); break;
                case 'N': _buffer.Append('\u0085'); break;
                case '_': _buffer.Append((char)0x00A0); break;
                case 'L': _buffer.Append((char)0x2028); break;
                case 'P': _buffer.Append((char)0x2029); break;
                case 'x': AppendCodePoint(ParseHex(2)); break;
                case 'u': AppendCodePoint(ParseHex(4)); break;
                case 'U': AppendCodePoint(ParseHex(8)); break;
                case '\n':
                case '\r':
                    // Escaped line break: join without a space and skip the next line's indentation.
                    _pos--;
                    ConsumeLineBreak();
                    SkipInlineSpace();
                    break;
                default:
                    _pos--;
                    throw Error($"Invalid escape sequence '\\{JsonParser.Describe(e)}'");
            }
        }
    }

    /// <summary>Folds a line break inside a quoted scalar: one break becomes a space, each empty line a newline.</summary>
    private void FoldQuotedLineBreak(int startLine, int startColumn)
    {
        // Trailing whitespace before the break is not content.
        var trim = _buffer.Length;
        while (trim > 0 && _buffer[trim - 1] is ' ' or '\t')
        {
            trim--;
        }

        _buffer.Length = trim;
        var breaks = 0;
        while (Peek() is '\n' or '\r')
        {
            ConsumeLineBreak();
            breaks++;
            if (Column == 0 && (IsDocumentMarker('-') || IsDocumentMarker('.')))
            {
                throw new SyntaxException("Unterminated quoted string (a document marker was reached)", startLine, startColumn);
            }

            SkipInlineSpace();
        }

        if (breaks == 1)
        {
            _buffer.Append(' ');
        }
        else
        {
            _buffer.Append('\n', breaks - 1);
        }
    }

    private int ParseHex(int digits)
    {
        var value = 0;
        for (var i = 0; i < digits; i++)
        {
            var h = JsonParser.HexValue(Peek());
            if (h < 0)
            {
                throw Error("Invalid hexadecimal escape sequence");
            }

            value = (value * 16) + h;
            _pos++;
        }

        return value;
    }

    private void AppendCodePoint(int codePoint)
    {
        if (codePoint > 0x10FFFF)
        {
            throw Error("Invalid Unicode code point in escape sequence");
        }

        if (codePoint <= 0xFFFF)
        {
            _buffer.Append((char)codePoint);
        }
        else
        {
            _buffer.Append(char.ConvertFromUtf32(codePoint));
        }
    }

    // ------------------------------------------------------------------ anchors, aliases, tags, merges

    private void ParseProperties(out string? anchor, out string? tag)
    {
        anchor = null;
        tag = null;
        while (true)
        {
            var c = Peek();
            if (c == '&' && anchor is null)
            {
                _pos++;
                anchor = ScanName("anchor");
                _anchors[anchor] = null; // in progress: an alias to it now would be recursive
            }
            else if (c == '!' && tag is null)
            {
                var start = _pos;
                if (Peek(1) == '<')
                {
                    while (_pos < _text.Length && _text[_pos] != '>' && !IsBlankOrEnd(_text[_pos]))
                    {
                        _pos++;
                    }

                    if (Peek() == '>')
                    {
                        _pos++;
                    }
                }
                else
                {
                    while (_pos < _text.Length && !IsBlankOrEnd(_text[_pos]) && !IsFlowIndicator(_text[_pos]))
                    {
                        _pos++;
                    }
                }

                tag = _text.Substring(start, _pos - start);
            }
            else
            {
                return;
            }

            if (Peek() is ' ' or '\t')
            {
                SkipInlineSpace();
            }
            else if (!AtLineEndOrComment() && !IsFlowIndicator(Peek()))
            {
                throw Error("A space is required after an anchor or tag");
            }
        }
    }

    private string ScanName(string what)
    {
        var start = _pos;
        while (_pos < _text.Length && !IsBlankOrEnd(_text[_pos]) && !IsFlowIndicator(_text[_pos]))
        {
            _pos++;
        }

        if (_pos == start)
        {
            throw Error($"An {what} name was expected");
        }

        return _text.Substring(start, _pos - start);
    }

    private DocNode ParseAlias()
    {
        _pos++; // '*'
        var location = Here;
        var name = ScanName("alias");
        if (!_anchors.TryGetValue(name, out var target))
        {
            throw new SyntaxException($"Alias '*{name}' refers to an undefined anchor", location.Line, location.Column);
        }

        if (target is null)
        {
            throw new SyntaxException($"Alias '*{name}' refers to its own enclosing node; recursive YAML structures are not supported", location.Line, location.Column, SyntaxErrorKind.Unsupported);
        }

        _aliasExpansion += target.Weight;
        if (_aliasExpansion > _limits.MaxExpandedNodes)
        {
            throw new SyntaxException(
                $"YAML aliases expand to more than {_limits.MaxExpandedNodes:N0} nodes; the document is rejected to prevent an expansion attack",
                location.Line,
                location.Column,
                SyntaxErrorKind.Limit);
        }

        return target;
    }

    private DocNode Complete(DocNode node, string? anchor, string? tag)
    {
        if (tag is not null && node is ScalarNode scalar)
        {
            node = ApplyTag(scalar, tag);
        }

        if (anchor is not null)
        {
            _anchors[anchor] = node;
        }

        return node;
    }

    private ScalarNode ApplyTag(ScalarNode scalar, string tag)
    {
        switch (tag)
        {
            case "!!str":
            case "!":
            case "!<tag:yaml.org,2002:str>":
                return scalar.Kind == NodeKind.String ? scalar : new ScalarNode(scalar.Location, NodeKind.String, scalar.Text);
            case "!!int":
            case "!!float":
            case "!!bool":
            case "!!null":
                var resolved = ResolvePlain(scalar.Location, scalar.Text);
                var expected = tag switch
                {
                    "!!bool" => NodeKind.Boolean,
                    "!!null" => NodeKind.Null,
                    _ => NodeKind.Number,
                };
                if (resolved.Kind != expected)
                {
                    throw new SyntaxException($"'{scalar.Text}' is not a valid {tag} value", scalar.Location.Line, scalar.Location.Column);
                }

                return resolved;
            default:
                // Application tags carry no meaning for API descriptions; the value is kept as resolved.
                return scalar;
        }
    }

    private void AddEntry(ObjectNode node, string key, bool keyIsPlain, SourceLocation keyLocation, DocNode value, ref List<DocNode>? merges)
    {
        if (keyIsPlain && key == "<<")
        {
            merges ??= [];
            merges.Add(value);
            return;
        }

        if (!node.Add(key, keyLocation, value))
        {
            _diagnostics.Report(DiagnosticDescriptors.DuplicateKey, keyLocation, null, key);
        }
    }

    private void ApplyMerges(ObjectNode node, List<DocNode>? merges)
    {
        if (merges is null)
        {
            return;
        }

        foreach (var merge in merges)
        {
            if (merge is ObjectNode source)
            {
                MergeFrom(node, source);
            }
            else if (merge is ArrayNode sources && sources.Items.All(i => i is ObjectNode))
            {
                foreach (var item in sources.Items)
                {
                    MergeFrom(node, (ObjectNode)item);
                }
            }
            else
            {
                throw new SyntaxException("A merge key ('<<') value must be a mapping or a sequence of mappings", merge.Location.Line, merge.Location.Column);
            }
        }
    }

    private static void MergeFrom(ObjectNode target, ObjectNode source)
    {
        foreach (var property in source.Properties)
        {
            if (!target.ContainsKey(property.Name))
            {
                target.Add(property.Name, property.KeyLocation, property.Value);
            }
        }
    }

    // ------------------------------------------------------------------ scalar resolution (YAML 1.2 core schema)

    private static ScalarNode ResolvePlain(SourceLocation location, string text)
    {
        switch (text)
        {
            case "":
            case "~":
            case "null":
            case "Null":
            case "NULL":
                return new ScalarNode(location, NodeKind.Null, "null", text);
            case "true":
            case "True":
            case "TRUE":
                return new ScalarNode(location, NodeKind.Boolean, "true", text);
            case "false":
            case "False":
            case "FALSE":
                return new ScalarNode(location, NodeKind.Boolean, "false", text);
        }

        var number = NormalizeNumber(text);
        return number is null
            ? new ScalarNode(location, NodeKind.String, text)
            : new ScalarNode(location, NodeKind.Number, number, text);
    }

    /// <summary>Returns the JSON number form of a YAML core-schema int or float, or null when it is not one.</summary>
    internal static string? NormalizeNumber(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'o'))
        {
            var hex = text[1] == 'x';
            ulong value = 0;
            for (var i = 2; i < text.Length; i++)
            {
                var digit = hex ? JsonParser.HexValue(text[i]) : (text[i] >= '0' && text[i] <= '7' ? text[i] - '0' : -1);
                if (digit < 0)
                {
                    return null;
                }

                var next = (value * (hex ? 16UL : 8UL)) + (ulong)digit;
                if (next / (hex ? 16UL : 8UL) != value)
                {
                    return null; // overflow: keep as a string
                }

                value = next;
            }

            return value.ToString(CultureInfo.InvariantCulture);
        }

        var pos = 0;
        var negative = false;
        if (text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            pos++;
        }

        var intStart = pos;
        while (pos < text.Length && text[pos] is >= '0' and <= '9')
        {
            pos++;
        }

        var intDigits = text.Substring(intStart, pos - intStart);
        var fraction = string.Empty;
        var hasPoint = false;
        if (pos < text.Length && text[pos] == '.')
        {
            hasPoint = true;
            pos++;
            var fracStart = pos;
            while (pos < text.Length && text[pos] is >= '0' and <= '9')
            {
                pos++;
            }

            fraction = text.Substring(fracStart, pos - fracStart);
        }

        if (intDigits.Length == 0 && fraction.Length == 0)
        {
            return null;
        }

        var exponent = string.Empty;
        if (pos < text.Length && text[pos] is 'e' or 'E')
        {
            var expStart = pos;
            pos++;
            if (pos < text.Length && text[pos] is '+' or '-')
            {
                pos++;
            }

            var digitsStart = pos;
            while (pos < text.Length && text[pos] is >= '0' and <= '9')
            {
                pos++;
            }

            if (pos == digitsStart)
            {
                return null;
            }

            exponent = text.Substring(expStart, pos - expStart);
        }

        if (pos != text.Length)
        {
            return null;
        }

        var integer = intDigits.TrimStart('0');
        if (integer.Length == 0)
        {
            integer = "0";
        }

        var builder = new StringBuilder();
        if (negative)
        {
            builder.Append('-');
        }

        builder.Append(integer);
        if (hasPoint && fraction.Length > 0)
        {
            builder.Append('.').Append(fraction);
        }

        builder.Append(exponent);
        var result = builder.ToString();
        return result == text ? text : result;
    }

    // ------------------------------------------------------------------ low-level scanning

    /// <summary>
    /// Skips blank and comment-only lines from the start of a line. Returns the indentation of the next line with
    /// content, leaving the position at the start of that line, or -1 at the end of input or at a document marker.
    /// </summary>
    private int PeekNextContentIndent()
    {
        while (_pos < _text.Length)
        {
            var i = _pos;
            var spaces = 0;
            while (i < _text.Length && _text[i] == ' ')
            {
                i++;
                spaces++;
            }

            var j = i;
            var sawTab = false;
            while (j < _text.Length && _text[j] is ' ' or '\t')
            {
                sawTab |= _text[j] == '\t';
                j++;
            }

            if (j >= _text.Length)
            {
                _pos = j;
                return -1;
            }

            var c = _text[j];
            if (c is '\n' or '\r')
            {
                _pos = j;
                ConsumeLineBreak();
                continue;
            }

            if (c == '#')
            {
                _pos = j;
                SkipToLineEnd();
                ConsumeLineBreak();
                continue;
            }

            if (spaces == 0 && IsDocumentMarkerAt(_pos))
            {
                return -1;
            }

            if (sawTab)
            {
                _pos = j;
                throw Error("Tabs are not allowed for indentation; use spaces");
            }

            return spaces;
        }

        return -1;
    }

    private void ExpectEndOfLine()
    {
        SkipInlineSpace();
        var c = Peek();
        if (c == '#')
        {
            SkipToLineEnd();
        }
        else if (c == ':')
        {
            throw Error("Mapping values are not allowed here; quote the value if it contains ': '");
        }
        else if (c != '\0' && c is not ('\n' or '\r'))
        {
            throw Error($"Unexpected '{JsonParser.Describe(c)}' after the value");
        }

        ConsumeLineBreak();
    }

    private bool AtLineEndOrComment()
    {
        var c = Peek();
        return c is '\0' or '\n' or '\r' or '#';
    }

    private void SkipInlineSpace()
    {
        while (_pos < _text.Length && _text[_pos] is ' ' or '\t')
        {
            _pos++;
        }
    }

    private void SkipFlowSpace()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c is ' ' or '\t')
            {
                _pos++;
            }
            else if (c is '\n' or '\r')
            {
                ConsumeLineBreak();
                if (Column == 0 && (IsDocumentMarker('-') || IsDocumentMarker('.')))
                {
                    throw Error("Unterminated flow collection (a document marker was reached)");
                }
            }
            else if (c == '#' && (_pos == _lineStart || _text[_pos - 1] is ' ' or '\t'))
            {
                SkipToLineEnd();
            }
            else
            {
                return;
            }
        }
    }

    private void SkipToLineEnd()
    {
        while (_pos < _text.Length && _text[_pos] is not ('\n' or '\r'))
        {
            _pos++;
        }
    }

    /// <summary>Consumes a line break at the current position. Returns false when at the end of input instead.</summary>
    private bool ConsumeLineBreak()
    {
        if (_pos >= _text.Length)
        {
            return false;
        }

        if (_text[_pos] == '\r')
        {
            _pos++;
            if (_pos < _text.Length && _text[_pos] == '\n')
            {
                _pos++;
            }
        }
        else if (_text[_pos] == '\n')
        {
            _pos++;
        }
        else
        {
            return false;
        }

        _line++;
        _lineStart = _pos;
        return true;
    }

    private bool IsDocumentMarker(char c) => Column == 0 && IsDocumentMarkerAt(_pos, c);

    private bool IsDocumentMarkerAt(int position) =>
        IsDocumentMarkerAt(position, '-') || IsDocumentMarkerAt(position, '.');

    private bool IsDocumentMarkerAt(int position, char c) =>
        position + 2 < _text.Length &&
        _text[position] == c && _text[position + 1] == c && _text[position + 2] == c &&
        (position + 3 >= _text.Length || IsBlankOrEnd(_text[position + 3]));

    private bool IsSequenceIndicatorAt(int position) =>
        position < _text.Length && _text[position] == '-' &&
        (position + 1 >= _text.Length || IsBlankOrEnd(_text[position + 1]));

    private char Peek(int offset = 0)
    {
        var index = _pos + offset;
        return index < _text.Length ? _text[index] : '\0';
    }

    private (int Pos, int Line, int LineStart) Save() => (_pos, _line, _lineStart);

    private void Restore((int Pos, int Line, int LineStart) state)
    {
        _pos = state.Pos;
        _line = state.Line;
        _lineStart = state.LineStart;
    }

    private void Enter()
    {
        if (++_depth > _limits.MaxDepth)
        {
            throw new SyntaxException($"The document is nested deeper than the limit of {_limits.MaxDepth}", _line, Column + 1, SyntaxErrorKind.Limit);
        }
    }

    private static bool IsBlankOrEnd(char c) => c is '\0' or ' ' or '\t' or '\n' or '\r';

    private static bool IsFlowIndicator(char c) => c is ',' or '[' or ']' or '{' or '}';

    private static bool IsPlainStart(char c, char next, bool inFlow)
    {
        switch (c)
        {
            case '\0':
            case '\n':
            case '\r':
            case ' ':
            case '\t':
                return false;
            case '-':
            case '?':
            case ':':
                return !IsBlankOrEnd(next) && !(inFlow && IsFlowIndicator(next));
            case ',':
            case '[':
            case ']':
            case '{':
            case '}':
            case '#':
            case '&':
            case '*':
            case '!':
            case '|':
            case '>':
            case '\'':
            case '"':
            case '%':
            case '@':
            case '`':
                return false;
            default:
                return true;
        }
    }

    private SyntaxException Error(string message) => new(message, _line, Column + 1);

    private SyntaxException Unsupported(string message) => new(message, _line, Column + 1, SyntaxErrorKind.Unsupported);
}
