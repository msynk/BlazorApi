namespace BlazorApi.OpenApi.Diagnostics;

/// <summary>
/// A position inside a source document. Lines and columns are one-based; <see cref="Line"/> is zero when the
/// position is unknown.
/// </summary>
public readonly struct SourceLocation : IEquatable<SourceLocation>
{
    /// <summary>Creates a location.</summary>
    /// <param name="source">The document path or URI, or <see langword="null"/> when unknown.</param>
    /// <param name="line">The one-based line, or zero when unknown.</param>
    /// <param name="column">The one-based column, or zero when unknown.</param>
    public SourceLocation(string? source, int line, int column)
    {
        Source = source;
        Line = line;
        Column = column;
    }

    /// <summary>An unknown location.</summary>
    public static SourceLocation None => default;

    /// <summary>The document path or URI.</summary>
    public string? Source { get; }

    /// <summary>The one-based line, or zero when unknown.</summary>
    public int Line { get; }

    /// <summary>The one-based column, or zero when unknown.</summary>
    public int Column { get; }

    /// <summary>Whether the location carries a line number.</summary>
    public bool HasPosition => Line > 0;

    /// <inheritdoc />
    public bool Equals(SourceLocation other) =>
        string.Equals(Source, other.Source, StringComparison.Ordinal) && Line == other.Line && Column == other.Column;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SourceLocation other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Source is null ? 0 : StringComparer.Ordinal.GetHashCode(Source);
            hash = (hash * 397) ^ Line;
            return (hash * 397) ^ Column;
        }
    }

    /// <summary>Formats the location the way MSBuild and compilers do: <c>file(line,column)</c>.</summary>
    public override string ToString()
    {
        var source = Source ?? "<input>";
        return HasPosition ? $"{source}({Line},{Column})" : source;
    }

    /// <summary>Compares two locations.</summary>
    public static bool operator ==(SourceLocation left, SourceLocation right) => left.Equals(right);

    /// <summary>Compares two locations.</summary>
    public static bool operator !=(SourceLocation left, SourceLocation right) => !left.Equals(right);
}
