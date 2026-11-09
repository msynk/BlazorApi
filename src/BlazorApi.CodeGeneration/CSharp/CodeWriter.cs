using System.Text;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>An indentation-aware text writer that always uses <c>\n</c> line endings (deterministic output).</summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public CodeWriter Line(string text = "")
    {
        if (text.Length > 0)
        {
            _builder.Append(' ', _indent * 4);
            _builder.Append(text);
        }

        _builder.Append('\n');
        return this;
    }

    public CodeWriter Lines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            Line(line);
        }

        return this;
    }

    public IDisposable Block(string header)
    {
        Line(header);
        Line("{");
        _indent++;
        return new Scope(this, "}");
    }

    public IDisposable Block(string header, string closing)
    {
        Line(header);
        Line("{");
        _indent++;
        return new Scope(this, closing);
    }

    public IDisposable Indent()
    {
        _indent++;
        return new Scope(this, null);
    }

    /// <summary>Writes an XML documentation element, splitting multi-line text and escaping markup.</summary>
    public CodeWriter Doc(string element, string? text, string? attribute = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return this;
        }

        var open = attribute is null ? $"<{element}>" : $"<{element} {attribute}>";
        // Every character C# treats as a line terminator becomes a real line break, so text from the contract can
        // never end the comment and inject code.
        var lines = NormalizeLineBreaks(Escape(text!)).Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();
        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 1)
        {
            return Line($"/// {open}{lines[0]}</{element}>");
        }

        Line("/// " + open);
        foreach (var line in lines)
        {
            Line(line.Length == 0 ? "/// <para/>" : "/// " + line);
        }

        return Line($"/// </{element}>");
    }

    private static string NormalizeLineBreaks(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace((char)0x2028, '\n').Replace((char)0x2029, '\n').Replace((char)0x85, '\n');

    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public override string ToString() => _builder.ToString();

    private sealed class Scope : IDisposable
    {
        private readonly CodeWriter _writer;
        private readonly string? _closing;

        public Scope(CodeWriter writer, string? closing)
        {
            _writer = writer;
            _closing = closing;
        }

        public void Dispose()
        {
            _writer._indent--;
            if (_closing is not null)
            {
                _writer.Line(_closing);
            }
        }
    }
}

/// <summary>C# literal helpers.</summary>
internal static class Literal
{
    /// <summary>A C# string literal.</summary>
    public static string String(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\0': builder.Append("\\0"); break;
                default:
                    if (c < 0x20 || c is (char)0x2028 or (char)0x2029 or (char)0x85)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
