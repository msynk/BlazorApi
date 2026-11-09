using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.IO;

namespace BlazorApi.OpenApi;

public static partial class OpenApiLoader
{
    /// <summary>
    /// Loads a document from a file. Relative references are read from the file system, restricted to the file's
    /// directory unless <see cref="OpenApiLoadOptions.ReferenceRoot"/> says otherwise.
    /// </summary>
    /// <remarks>This method performs file I/O and is therefore not part of the Roslyn source generator.</remarks>
    public static OpenApiLoadResult LoadFile(string path, OpenApiLoadOptions? options = null)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        options ??= OpenApiLoadOptions.Default;
        var fullPath = Path.GetFullPath(path);
        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var diagnostic = DiagnosticDescriptors.SourceUnavailable.Create(new SourceLocation(path, 0, 0), null, path, ex.Message);
            return new OpenApiLoadResult(null, [diagnostic], []);
        }

        if (options.DocumentResolver is null)
        {
            options = options with { DocumentResolver = FileSystemDocumentResolver.Instance };
        }

        return Parse(text, fullPath, options);
    }
}
