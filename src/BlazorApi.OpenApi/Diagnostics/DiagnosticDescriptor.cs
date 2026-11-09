using System.Globalization;

namespace BlazorApi.OpenApi.Diagnostics;

/// <summary>Describes a kind of diagnostic: its stable identifier, default severity and message template.</summary>
public sealed class DiagnosticDescriptor
{
    /// <summary>Creates a descriptor.</summary>
    public DiagnosticDescriptor(string id, string title, string messageFormat, DiagnosticSeverity defaultSeverity, string category)
    {
        Id = id;
        Title = title;
        MessageFormat = messageFormat;
        DefaultSeverity = defaultSeverity;
        Category = category;
    }

    /// <summary>The stable identifier.</summary>
    public string Id { get; }

    /// <summary>A short title.</summary>
    public string Title { get; }

    /// <summary>A composite format string for the message.</summary>
    public string MessageFormat { get; }

    /// <summary>The default severity.</summary>
    public DiagnosticSeverity DefaultSeverity { get; }

    /// <summary>The category (Syntax, Reference, Validation, Model, Configuration, Generation, Compatibility).</summary>
    public string Category { get; }

    /// <summary>Creates a diagnostic from this descriptor.</summary>
    public ApiDiagnostic Create(SourceLocation location, string? pointer, params object?[] args) =>
        new(Id, DefaultSeverity, Format(args), location, pointer);

    /// <summary>Creates a diagnostic from this descriptor with an explicit severity.</summary>
    public ApiDiagnostic Create(DiagnosticSeverity severity, SourceLocation location, string? pointer, params object?[] args) =>
        new(Id, severity, Format(args), location, pointer);

    private string Format(object?[] args) =>
        args.Length == 0 ? MessageFormat : string.Format(CultureInfo.InvariantCulture, MessageFormat, args);
}
