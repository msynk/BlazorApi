using System.Globalization;

namespace BlazorApi.OpenApi.Diagnostics;

/// <summary>The severity of an <see cref="ApiDiagnostic"/>.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational; never fails a build.</summary>
    Info,

    /// <summary>Something is likely wrong or was handled in a degraded way (for example mapped to a less precise type).</summary>
    Warning,

    /// <summary>The input cannot be processed correctly.</summary>
    Error,
}

/// <summary>A problem found while loading, validating or generating from an API contract.</summary>
public sealed class ApiDiagnostic
{
    /// <summary>Creates a diagnostic.</summary>
    public ApiDiagnostic(string id, DiagnosticSeverity severity, string message, SourceLocation location, string? pointer = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Severity = severity;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Location = location;
        Pointer = pointer;
    }

    /// <summary>The stable diagnostic identifier, for example <c>BAPI1001</c>. See <see cref="DiagnosticDescriptors"/>.</summary>
    public string Id { get; }

    /// <summary>The severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>The human-readable message.</summary>
    public string Message { get; }

    /// <summary>Where in the source document the problem is.</summary>
    public SourceLocation Location { get; }

    /// <summary>The JSON pointer (for example <c>#/components/schemas/Pet</c>) of the element concerned, when known.</summary>
    public string? Pointer { get; }

    /// <summary>Whether this diagnostic is an error.</summary>
    public bool IsError => Severity == DiagnosticSeverity.Error;

    /// <summary>
    /// Formats the diagnostic in the canonical MSBuild format (<c>file(line,col): error BAPI1001: message</c>) so build
    /// hosts and IDEs can parse it.
    /// </summary>
    public override string ToString()
    {
        var severity = Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        };
        var pointer = Pointer is null ? string.Empty : string.Format(CultureInfo.InvariantCulture, " [{0}]", Pointer);
        return string.Format(CultureInfo.InvariantCulture, "{0}: {1} {2}: {3}{4}", Location, severity, Id, Message, pointer);
    }
}
