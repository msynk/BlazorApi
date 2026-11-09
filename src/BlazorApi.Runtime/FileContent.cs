using System.Net.Http.Headers;

namespace BlazorApi.Runtime;

/// <summary>A file (or any binary payload) sent as a request body or a multipart part.</summary>
public sealed class FileContent
{
    /// <summary>Creates file content from a stream. The stream is read when the request is sent and not disposed.</summary>
    public FileContent(Stream content, string? fileName = null, string? contentType = null)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        FileName = fileName;
        ContentType = contentType;
    }

    /// <summary>The content stream.</summary>
    public Stream Content { get; }

    /// <summary>The file name sent in multipart bodies.</summary>
    public string? FileName { get; }

    /// <summary>The media type; <c>application/octet-stream</c> when not set.</summary>
    public string? ContentType { get; }

    /// <summary>Creates file content from bytes.</summary>
    public static FileContent FromBytes(byte[] bytes, string? fileName = null, string? contentType = null) =>
        new(new MemoryStream(bytes ?? throw new ArgumentNullException(nameof(bytes)), writable: false), fileName, contentType);

    /// <summary>Creates the HTTP content.</summary>
    public HttpContent ToHttpContent(string? defaultContentType = null)
    {
        var content = new StreamContent(Content);
        var type = ContentType ?? defaultContentType ?? "application/octet-stream";
        if (MediaTypeHeaderValue.TryParse(type, out var mediaType))
        {
            content.Headers.ContentType = mediaType;
        }

        return content;
    }
}

/// <summary>
/// A binary response whose body is streamed. Dispose it to release the connection.
/// </summary>
public sealed class FileResponse : IDisposable, IAsyncDisposable
{
    private readonly HttpResponseMessage _response;

    internal FileResponse(HttpResponseMessage response, Stream content)
    {
        _response = response;
        Content = content;
        StatusCode = (int)response.StatusCode;
        ContentType = response.Content.Headers.ContentType?.ToString();
        ContentLength = response.Content.Headers.ContentLength;
        FileName = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"') ??
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Headers = ApiHeaders.From(response);
    }

    /// <summary>The body stream.</summary>
    public Stream Content { get; }

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>The media type of the body.</summary>
    public string? ContentType { get; }

    /// <summary>The body length, when known.</summary>
    public long? ContentLength { get; }

    /// <summary>The file name from <c>Content-Disposition</c>, when present.</summary>
    public string? FileName { get; }

    /// <summary>The response headers.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <summary>Reads the whole body into memory.</summary>
    public async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Content.Dispose();
        _response.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
    }
}
