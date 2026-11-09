using BlazorApi.OpenApi.Diagnostics;

namespace BlazorApi.OpenApi.Syntax;

/// <summary>The serialization format of a document.</summary>
public enum DocumentFormat
{
    /// <summary>Detect from the file extension, then from the content.</summary>
    Auto,

    /// <summary>JSON (RFC 8259).</summary>
    Json,

    /// <summary>YAML 1.2.</summary>
    Yaml,
}

/// <summary>Parses JSON and YAML text into a <see cref="DocNode"/> tree.</summary>
public static class SyntaxParser
{
    /// <summary>
    /// Parses a document. Syntax errors are reported to <paramref name="diagnostics"/> and yield
    /// <see langword="null"/>; this method does not throw for malformed input.
    /// </summary>
    /// <param name="text">The document text.</param>
    /// <param name="source">The document path or URI, used in diagnostics and for format detection.</param>
    /// <param name="format">The format, or <see cref="DocumentFormat.Auto"/>.</param>
    /// <param name="diagnostics">Receives syntax diagnostics.</param>
    /// <param name="limits">Safety limits; <see cref="ParserLimits.Default"/> when omitted.</param>
    public static DocNode? Parse(string text, string? source, DocumentFormat format, DiagnosticBag diagnostics, ParserLimits? limits = null)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        limits ??= ParserLimits.Default;
        var location = new SourceLocation(source, 0, 0);
        if (text.Length > limits.MaxDocumentLength)
        {
            diagnostics.Report(DiagnosticDescriptors.LimitExceeded, location, null,
                $"The document has {text.Length:N0} characters, more than the limit of {limits.MaxDocumentLength:N0}");
            return null;
        }

        if (format == DocumentFormat.Auto)
        {
            format = DetectFormat(text, source);
        }

        if (format == DocumentFormat.Json)
        {
            var jsonDiagnostics = new DiagnosticBag();
            var json = TryParse(() => JsonParser.Parse(text, source, limits, jsonDiagnostics), source, jsonDiagnostics);
            if (json is not null || IsDeclaredJson(source))
            {
                diagnostics.AddRange(jsonDiagnostics);
                return json;
            }

            // Content that merely looks like JSON (YAML flow style, for example) gets a second chance as YAML; when
            // that fails too, the JSON error is the more useful one to report.
            var yamlDiagnostics = new DiagnosticBag();
            var yaml = TryParse(() => YamlParser.Parse(text, source, limits, yamlDiagnostics), source, yamlDiagnostics);
            diagnostics.AddRange(yaml is not null ? yamlDiagnostics : jsonDiagnostics);
            return yaml;
        }

        return TryParse(() => YamlParser.Parse(text, source, limits, diagnostics), source, diagnostics);
    }

    /// <summary>Detects the format from the source extension, falling back to the first significant character.</summary>
    public static DocumentFormat DetectFormat(string text, string? source)
    {
        if (IsDeclaredJson(source))
        {
            return DocumentFormat.Json;
        }

        var extension = GetExtension(source);
        if (extension is ".yaml" or ".yml")
        {
            return DocumentFormat.Yaml;
        }

        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or (char)0xFEFF)
            {
                continue;
            }

            return c is '{' or '[' ? DocumentFormat.Json : DocumentFormat.Yaml;
        }

        return DocumentFormat.Yaml;
    }

    private static bool IsDeclaredJson(string? source) => GetExtension(source) == ".json";

    private static string GetExtension(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var path = source!;
        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            path = path.Substring(0, query);
        }

        var dot = path.LastIndexOf('.');
        var slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return dot > slash ? path.Substring(dot).ToLowerInvariant() : string.Empty;
    }

    private static DocNode? TryParse(Func<DocNode> parse, string? source, DiagnosticBag diagnostics)
    {
        try
        {
            return parse();
        }
        catch (SyntaxException ex)
        {
            var descriptor = ex.Kind switch
            {
                SyntaxErrorKind.Limit => DiagnosticDescriptors.LimitExceeded,
                SyntaxErrorKind.Unsupported => DiagnosticDescriptors.UnsupportedYamlFeature,
                _ => DiagnosticDescriptors.SyntaxError,
            };
            diagnostics.Report(descriptor, new SourceLocation(source, ex.Line, ex.Column), null, ex.Message);
            return null;
        }
    }
}
