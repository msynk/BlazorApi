namespace BlazorApi.OpenApi.Syntax;

/// <summary>
/// Safety limits applied when parsing untrusted documents. The defaults accommodate the largest public API
/// descriptions (tens of megabytes) while bounding memory and CPU use.
/// </summary>
public sealed record ParserLimits
{
    /// <summary>The default limits.</summary>
    public static ParserLimits Default { get; } = new();

    /// <summary>The maximum document length in characters. Default: 64 MiB.</summary>
    public int MaxDocumentLength { get; init; } = 64 * 1024 * 1024;

    /// <summary>The maximum nesting depth of objects and arrays. Default: 256.</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>
    /// The maximum number of nodes a document may expand to when YAML aliases are counted at every use, which
    /// prevents "billion laughs" expansion attacks. Default: 10,000,000.
    /// </summary>
    public long MaxExpandedNodes { get; init; } = 10_000_000;
}

/// <summary>The category of a <see cref="SyntaxException"/>.</summary>
internal enum SyntaxErrorKind
{
    Syntax,
    Limit,
    Unsupported,
}

/// <summary>A syntax error with its position, raised by the parsers and turned into a diagnostic.</summary>
internal sealed class SyntaxException : Exception
{
    public SyntaxException(string message, int line, int column, SyntaxErrorKind kind = SyntaxErrorKind.Syntax)
        : base(message)
    {
        Line = line;
        Column = column;
        Kind = kind;
    }

    public int Line { get; }

    public int Column { get; }

    public SyntaxErrorKind Kind { get; }
}
