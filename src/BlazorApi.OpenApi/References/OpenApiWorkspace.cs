using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.References;

/// <summary>
/// Resolves <c>$ref</c> values across the root document and the external documents it references, enforcing the
/// external reference policy and detecting reference cycles.
/// </summary>
internal sealed class OpenApiWorkspace
{
    private readonly OpenApiLoadOptions _options;
    private readonly DiagnosticBag _diagnostics;
    private readonly LoadedDocument _root;
    private readonly Uri _referenceRoot;
    private readonly Dictionary<string, LoadedDocument?> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedDocument> _documentsByPrefix = new(StringComparer.Ordinal);
    private readonly List<Uri> _external = [];

    public OpenApiWorkspace(Uri rootUri, DocNode rootNode, OpenApiReader rootReader, OpenApiLoadOptions options, DiagnosticBag diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
        _root = new LoadedDocument(rootUri, string.Empty, rootNode, rootReader);
        _documents[rootUri.AbsoluteUri] = _root;
        _documentsByPrefix[string.Empty] = _root;
        _referenceRoot = options.ReferenceRoot is { Length: > 0 } root
            ? DocumentUris.FromPath(root.EndsWith("/", StringComparison.Ordinal) || root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "/")
            : DocumentUris.DirectoryOf(rootUri);
    }

    public IReadOnlyList<Uri> ExternalDocuments => _external;

    public void ResolveReferences(OpenApiDocument document)
    {
        // Breadth over everything reachable; targets read from other documents (or from parts of the root document
        // that were not read yet) join the work list so their own references are resolved too.
        var visited = new HashSet<OpenApiElement>(new IdentityComparer());
        var queue = new Queue<OpenApiElement>();
        var references = new List<OpenApiElement>();
        queue.Enqueue(document);
        while (queue.Count > 0)
        {
            var element = queue.Dequeue();
            if (!visited.Add(element))
            {
                continue;
            }

            if (OpenApiWalker.ReferenceOf(element) is not null)
            {
                references.Add(element);
                if (Resolve(element) is { } target)
                {
                    queue.Enqueue(target);
                }
            }

            foreach (var child in OpenApiWalker.Children(element))
            {
                queue.Enqueue(child);
            }
        }

        DetectCycles(references);
    }

    private OpenApiElement? Resolve(OpenApiElement element)
    {
        return element switch
        {
            OpenApiSchema s => s.ReferenceTarget = ResolveAs<OpenApiSchema>(s, s.Reference!),
            OpenApiParameter p => p.ReferenceTarget = ResolveAs<OpenApiParameter>(p, p.Reference!),
            OpenApiHeader h => h.ReferenceTarget = ResolveAs<OpenApiHeader>(h, h.Reference!),
            OpenApiResponse r => r.ReferenceTarget = ResolveAs<OpenApiResponse>(r, r.Reference!),
            OpenApiRequestBody b => b.ReferenceTarget = ResolveAs<OpenApiRequestBody>(b, b.Reference!),
            OpenApiExample e => e.ReferenceTarget = ResolveAs<OpenApiExample>(e, e.Reference!),
            OpenApiSecurityScheme s => s.ReferenceTarget = ResolveAs<OpenApiSecurityScheme>(s, s.Reference!),
            OpenApiPathItem p => p.ReferenceTarget = ResolveAs<OpenApiPathItem>(p, p.Reference!),
            _ => null,
        };
    }

    private T? ResolveAs<T>(OpenApiElement source, string reference)
        where T : OpenApiElement
    {
        var refPointer = source.Pointer + "/$ref";
        if (reference.Length == 0)
        {
            _diagnostics.Report(DiagnosticDescriptors.InvalidReference, source.Location, refPointer, reference, "the reference is empty");
            return null;
        }

        var hash = reference.IndexOf('#');
        var documentPart = hash >= 0 ? reference.Substring(0, hash) : reference;
        var fragment = hash >= 0 ? reference.Substring(hash + 1) : string.Empty;

        var origin = DocumentOf(source);
        LoadedDocument? target;
        if (documentPart.Length == 0)
        {
            target = origin;
        }
        else
        {
            target = LoadExternal(origin, documentPart, reference, source.Location, refPointer);
            if (target is null)
            {
                return null;
            }
        }

        var node = JsonPointer.Evaluate(target.Root, fragment, out var error);
        if (node is null)
        {
            var descriptor = error is not null && error.Contains("anchor")
                ? DiagnosticDescriptors.UnsupportedReference
                : DiagnosticDescriptors.UnresolvedReference;
            _diagnostics.Report(descriptor, source.Location, refPointer, reference, error);
            return null;
        }

        return target.Reader.ReadReferenceTarget<T>(node, "#" + fragment);
    }

    private LoadedDocument? LoadExternal(LoadedDocument origin, string documentPart, string reference, SourceLocation location, string pointer)
    {
        if (_options.ExternalReferences == ExternalReferencePolicy.Disabled)
        {
            _diagnostics.Report(DiagnosticDescriptors.ReferenceNotAllowed, location, pointer, reference, "external references are disabled");
            return null;
        }

        Uri uri;
        try
        {
            uri = new Uri(origin.Uri, documentPart);
        }
        catch (UriFormatException ex)
        {
            _diagnostics.Report(DiagnosticDescriptors.InvalidReference, location, pointer, reference, ex.Message);
            return null;
        }

        if (uri.Scheme is "http" or "https")
        {
            if (_options.ExternalReferences != ExternalReferencePolicy.LocalFilesAndRemote)
            {
                _diagnostics.Report(DiagnosticDescriptors.ReferenceNotAllowed, location, pointer, reference,
                    "remote references are disabled; download the document and reference a local copy, or enable remote references");
                return null;
            }
        }
        else if (uri.Scheme != "file")
        {
            _diagnostics.Report(DiagnosticDescriptors.ReferenceNotAllowed, location, pointer, reference, $"the '{uri.Scheme}' scheme is not supported");
            return null;
        }
        else if (!DocumentUris.IsUnder(uri, _referenceRoot))
        {
            _diagnostics.Report(DiagnosticDescriptors.ReferenceNotAllowed, location, pointer, reference,
                "the file is outside the reference root directory (set the reference root to allow it)");
            return null;
        }

        var key = uri.AbsoluteUri;
        if (_documents.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_documents.Count > _options.MaxExternalDocuments)
        {
            _diagnostics.Report(DiagnosticDescriptors.LimitExceeded, location, pointer,
                $"More than {_options.MaxExternalDocuments} external documents are referenced");
            _documents[key] = null;
            return null;
        }

        if (_options.DocumentResolver is null)
        {
            _diagnostics.Report(DiagnosticDescriptors.SourceUnavailable, location, pointer, reference, "no document resolver is configured for external references");
            _documents[key] = null;
            return null;
        }

        if (!_options.DocumentResolver.TryRead(uri, out var content, out var readError))
        {
            _diagnostics.Report(DiagnosticDescriptors.SourceUnavailable, location, pointer, reference, readError ?? "the document cannot be read");
            _documents[key] = null;
            return null;
        }

        _external.Add(uri);
        var displayName = DocumentUris.DisplayName(uri, _root.Uri);
        var node = SyntaxParser.Parse(content, displayName, _options.Format, _diagnostics, _options.Limits);
        if (node is null)
        {
            _documents[key] = null;
            return null;
        }

        // Fragments (plain schema files) have no 'openapi' field and follow the root document's version.
        var version = _root.Reader.Version;
        if (node is ObjectNode obj && obj.ContainsKey("openapi"))
        {
            version = OpenApiReader.DetectVersion(node, _diagnostics, out _) ?? version;
        }

        var loaded = new LoadedDocument(uri, displayName, node, new OpenApiReader(_diagnostics, version, displayName));
        _documents[key] = loaded;
        _documentsByPrefix[displayName] = loaded;
        return loaded;
    }

    private LoadedDocument DocumentOf(OpenApiElement element)
    {
        var hash = element.Pointer.IndexOf('#');
        var prefix = hash > 0 ? element.Pointer.Substring(0, hash) : string.Empty;
        return _documentsByPrefix.TryGetValue(prefix, out var document) ? document : _root;
    }

    private void DetectCycles(List<OpenApiElement> references)
    {
        foreach (var start in references)
        {
            var seen = new HashSet<OpenApiElement>(new IdentityComparer()) { start };
            var current = OpenApiWalker.ReferenceTargetOf(start);
            while (current is not null && OpenApiWalker.ReferenceOf(current) is not null)
            {
                if (!seen.Add(current))
                {
                    _diagnostics.Report(DiagnosticDescriptors.ReferenceCycle, start.Location, start.Pointer + "/$ref", OpenApiWalker.ReferenceOf(start));
                    ClearTarget(start);
                    break;
                }

                current = OpenApiWalker.ReferenceTargetOf(current);
            }
        }
    }

    private static void ClearTarget(OpenApiElement element)
    {
        switch (element)
        {
            case OpenApiSchema s: s.ReferenceTarget = null; break;
            case OpenApiParameter p: p.ReferenceTarget = null; break;
            case OpenApiHeader h: h.ReferenceTarget = null; break;
            case OpenApiResponse r: r.ReferenceTarget = null; break;
            case OpenApiRequestBody b: b.ReferenceTarget = null; break;
            case OpenApiExample e: e.ReferenceTarget = null; break;
            case OpenApiSecurityScheme s: s.ReferenceTarget = null; break;
            case OpenApiPathItem p: p.ReferenceTarget = null; break;
        }
    }

    private sealed class LoadedDocument
    {
        public LoadedDocument(Uri uri, string displayName, DocNode root, OpenApiReader reader)
        {
            Uri = uri;
            DisplayName = displayName;
            Root = root;
            Reader = reader;
        }

        public Uri Uri { get; }

        public string DisplayName { get; }

        public DocNode Root { get; }

        public OpenApiReader Reader { get; }
    }

    private sealed class IdentityComparer : IEqualityComparer<OpenApiElement>
    {
        public bool Equals(OpenApiElement? x, OpenApiElement? y) => ReferenceEquals(x, y);

        public int GetHashCode(OpenApiElement obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
