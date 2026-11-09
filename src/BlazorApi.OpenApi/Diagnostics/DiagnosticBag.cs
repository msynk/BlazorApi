using System.Collections;

namespace BlazorApi.OpenApi.Diagnostics;

/// <summary>An ordered, append-only collection of diagnostics with severity-override support.</summary>
public sealed class DiagnosticBag : IReadOnlyList<ApiDiagnostic>
{
    private readonly List<ApiDiagnostic> _items = [];
    private readonly IReadOnlyDictionary<string, DiagnosticSeverity?>? _overrides;
    private int _errorCount;

    /// <summary>Creates an empty bag.</summary>
    /// <param name="severityOverrides">
    /// Optional per-identifier severity overrides. A <see langword="null"/> value suppresses the diagnostic entirely.
    /// </param>
    public DiagnosticBag(IReadOnlyDictionary<string, DiagnosticSeverity?>? severityOverrides = null) =>
        _overrides = severityOverrides;

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <summary>Whether any error was reported.</summary>
    public bool HasErrors => _errorCount > 0;

    /// <summary>The number of errors reported.</summary>
    public int ErrorCount => _errorCount;

    /// <inheritdoc />
    public ApiDiagnostic this[int index] => _items[index];

    /// <summary>Adds a diagnostic, applying any configured severity override.</summary>
    public void Add(ApiDiagnostic diagnostic)
    {
        if (diagnostic is null)
        {
            throw new ArgumentNullException(nameof(diagnostic));
        }

        if (_overrides is not null && _overrides.TryGetValue(diagnostic.Id, out var severity))
        {
            if (severity is null)
            {
                return;
            }

            if (severity.Value != diagnostic.Severity)
            {
                diagnostic = new ApiDiagnostic(diagnostic.Id, severity.Value, diagnostic.Message, diagnostic.Location, diagnostic.Pointer);
            }
        }

        _items.Add(diagnostic);
        if (diagnostic.IsError)
        {
            _errorCount++;
        }
    }

    /// <summary>Adds several diagnostics.</summary>
    public void AddRange(IEnumerable<ApiDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Add(diagnostic);
        }
    }

    /// <summary>Creates and adds a diagnostic from a descriptor.</summary>
    public void Report(DiagnosticDescriptor descriptor, SourceLocation location, string? pointer, params object?[] args) =>
        Add(descriptor.Create(location, pointer, args));

    /// <summary>
    /// Returns the diagnostics sorted deterministically by source, line, column and identifier, so output does not
    /// depend on processing order.
    /// </summary>
    public IReadOnlyList<ApiDiagnostic> ToSortedList()
    {
        var copy = _items.ToArray();
        // A stable sort (LINQ OrderBy) keeps equal entries in report order.
        return copy
            .OrderBy(d => d.Location.Source ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Location.Line)
            .ThenBy(d => d.Location.Column)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public IEnumerator<ApiDiagnostic> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
