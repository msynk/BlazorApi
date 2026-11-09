using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.References;
using BlazorApi.OpenApi.Syntax;
using BlazorApi.OpenApi.Validation;

namespace BlazorApi.OpenApi;

/// <summary>Which <c>$ref</c> targets outside the document being loaded may be followed.</summary>
public enum ExternalReferencePolicy
{
    /// <summary>Only references within the same document.</summary>
    Disabled,

    /// <summary>Local files under <see cref="OpenApiLoadOptions.ReferenceRoot"/> (the default).</summary>
    LocalFiles,

    /// <summary>Local files and http(s) URLs (the document resolver must be able to fetch them).</summary>
    LocalFilesAndRemote,
}

/// <summary>Options for loading an OpenAPI document.</summary>
public sealed record OpenApiLoadOptions
{
    /// <summary>The default options.</summary>
    public static OpenApiLoadOptions Default { get; } = new();

    /// <summary>The document format. Default: detect.</summary>
    public DocumentFormat Format { get; init; } = DocumentFormat.Auto;

    /// <summary>Parser safety limits.</summary>
    public ParserLimits Limits { get; init; } = ParserLimits.Default;

    /// <summary>Which external references may be followed. Default: local files under the reference root.</summary>
    public ExternalReferencePolicy ExternalReferences { get; init; } = ExternalReferencePolicy.LocalFiles;

    /// <summary>
    /// The directory that referenced local files must be inside (path traversal protection). Default: the directory
    /// of the root document.
    /// </summary>
    public string? ReferenceRoot { get; init; }

    /// <summary>Supplies the content of referenced documents. Without one, external references cannot be read.</summary>
    public IDocumentResolver? DocumentResolver { get; init; }

    /// <summary>The maximum number of external documents one load may read. Default: 256.</summary>
    public int MaxExternalDocuments { get; init; } = 256;

    /// <summary>Whether to run semantic validation after loading. Default: true.</summary>
    public bool Validate { get; init; } = true;

    /// <summary>
    /// Per-diagnostic severity overrides keyed by identifier (<c>BAPI3009</c>); a <see langword="null"/> value
    /// suppresses the diagnostic.
    /// </summary>
    public IReadOnlyDictionary<string, DiagnosticSeverity?>? SeverityOverrides { get; init; }
}

/// <summary>The result of loading an OpenAPI document.</summary>
public sealed class OpenApiLoadResult
{
    internal OpenApiLoadResult(OpenApiDocument? document, IReadOnlyList<ApiDiagnostic> diagnostics, IReadOnlyList<Uri> externalDocuments)
    {
        Document = document;
        Diagnostics = diagnostics;
        ExternalDocuments = externalDocuments;
    }

    /// <summary>The document, or <see langword="null"/> when it could not be parsed or has no supported version.</summary>
    public OpenApiDocument? Document { get; }

    /// <summary>All diagnostics, sorted by location.</summary>
    public IReadOnlyList<ApiDiagnostic> Diagnostics { get; }

    /// <summary>Whether any diagnostic is an error.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    /// <summary>The external documents that were read while resolving references (for dependency tracking).</summary>
    public IReadOnlyList<Uri> ExternalDocuments { get; }
}

/// <summary>Loads OpenAPI 3.0 and 3.1 documents: parsing, reference resolution and semantic validation.</summary>
public static partial class OpenApiLoader
{
    /// <summary>The base URI given to documents loaded from a relative or missing path.</summary>
    public const string VirtualRoot = "file:///openapi-virtual-root/";

    /// <summary>
    /// Loads a document from text. Never throws for invalid input: problems are reported as diagnostics.
    /// </summary>
    /// <param name="text">The JSON or YAML text.</param>
    /// <param name="documentPath">
    /// The document path or URI: used in diagnostics, for format detection and as the base of relative references.
    /// </param>
    /// <param name="options">Options; <see cref="OpenApiLoadOptions.Default"/> when omitted.</param>
    public static OpenApiLoadResult Parse(string text, string? documentPath = null, OpenApiLoadOptions? options = null)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        options ??= OpenApiLoadOptions.Default;
        var diagnostics = new DiagnosticBag(options.SeverityOverrides);
        var source = documentPath ?? "openapi";
        var root = SyntaxParser.Parse(text, source, options.Format, diagnostics, options.Limits);
        if (root is null)
        {
            return new OpenApiLoadResult(null, diagnostics.ToSortedList(), []);
        }

        var version = OpenApiReader.DetectVersion(root, diagnostics, out var versionText);
        if (version is null)
        {
            return new OpenApiLoadResult(null, diagnostics.ToSortedList(), []);
        }

        var reader = new OpenApiReader(diagnostics, version.Value);
        var document = reader.ReadDocument((ObjectNode)root, versionText);
        var workspace = new OpenApiWorkspace(DocumentUris.FromPath(source), root, reader, options, diagnostics);
        workspace.ResolveReferences(document);
        if (options.Validate)
        {
            OpenApiValidator.Validate(document, diagnostics);
        }

        return new OpenApiLoadResult(document, diagnostics.ToSortedList(), workspace.ExternalDocuments);
    }
}
