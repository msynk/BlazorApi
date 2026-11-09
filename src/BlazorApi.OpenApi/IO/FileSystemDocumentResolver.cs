using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.References;

namespace BlazorApi.OpenApi.IO;

/// <summary>
/// Reads referenced documents from the local file system. Remote (http) documents are not fetched. The reference
/// policy (<see cref="OpenApiLoadOptions.ReferenceRoot"/>) is checked before this resolver is called.
/// </summary>
/// <remarks>This type performs file I/O and is therefore not part of the Roslyn source generator.</remarks>
public sealed class FileSystemDocumentResolver : IDocumentResolver
{
    /// <summary>A shared instance.</summary>
    public static FileSystemDocumentResolver Instance { get; } = new();

    /// <inheritdoc />
    public bool TryRead(Uri uri, out string content, out string? error)
    {
        content = string.Empty;
        if (!uri.IsFile)
        {
            error = "only local files can be read; remote documents must be downloaded first";
            return false;
        }

        try
        {
            content = File.ReadAllText(uri.LocalPath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }
}
