namespace BlazorApi.OpenApi.References;

/// <summary>
/// Supplies the content of documents referenced by <c>$ref</c>. Hosts choose the implementation: the command line
/// reads the file system (<c>FileSystemDocumentResolver</c>), the source generator serves MSBuild AdditionalFiles, and
/// tests use <see cref="InMemoryDocumentResolver"/>.
/// </summary>
public interface IDocumentResolver
{
    /// <summary>Reads a document.</summary>
    /// <param name="uri">The absolute document URI (a <c>file:</c> URI for local files).</param>
    /// <param name="content">The document text.</param>
    /// <param name="error">Why the document could not be read.</param>
    /// <returns>Whether the document was read.</returns>
    bool TryRead(Uri uri, out string content, out string? error);
}

/// <summary>Serves documents from memory, keyed by absolute URI.</summary>
public sealed class InMemoryDocumentResolver : IDocumentResolver
{
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);

    /// <summary>Adds a document. Relative paths are placed under <see cref="OpenApiLoader.VirtualRoot"/>.</summary>
    public InMemoryDocumentResolver Add(string path, string content)
    {
        _documents[DocumentUris.FromPath(path).AbsoluteUri] = content;
        return this;
    }

    /// <inheritdoc />
    public bool TryRead(Uri uri, out string content, out string? error)
    {
        if (_documents.TryGetValue(uri.AbsoluteUri, out var found))
        {
            content = found;
            error = null;
            return true;
        }

        content = string.Empty;
        error = "the document is not available";
        return false;
    }
}

/// <summary>Helpers for document URIs.</summary>
internal static class DocumentUris
{
    /// <summary>
    /// Converts a path to an absolute URI. Absolute paths become <c>file:</c> URIs; relative paths are placed under a
    /// virtual root so in-memory documents can reference each other without touching the file system.
    /// </summary>
    public static Uri FromPath(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && (absolute.Scheme is "http" or "https" or "file") && !IsWindowsDrivePath(path))
        {
            return absolute;
        }

        if (IsWindowsDrivePath(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return new Uri(path);
        }

        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return new Uri("file://" + path);
        }

        return new Uri(new Uri(OpenApiLoader.VirtualRoot), path.Replace('\\', '/'));
    }

    /// <summary>Whether the URI lies under the root directory URI.</summary>
    public static bool IsUnder(Uri uri, Uri root)
    {
        var rootText = root.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? root.AbsoluteUri : root.AbsoluteUri + "/";
        return uri.AbsoluteUri.StartsWith(rootText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The URI of the directory containing a document.</summary>
    public static Uri DirectoryOf(Uri document) => new(document, ".");

    /// <summary>A short display name of a document relative to a base document.</summary>
    public static string DisplayName(Uri document, Uri baseDocument)
    {
        if (document.Scheme == baseDocument.Scheme && document.Host == baseDocument.Host)
        {
            var relative = DirectoryOf(baseDocument).MakeRelativeUri(document).OriginalString;
            return Uri.UnescapeDataString(relative);
        }

        return document.AbsoluteUri;
    }

    private static bool IsWindowsDrivePath(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
}
