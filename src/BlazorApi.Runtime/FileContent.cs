using System.Net;
using System.Net.Http.Headers;

namespace BlazorApi.Runtime;

/// <summary>The progress of an upload or download.</summary>
/// <param name="Transferred">The bytes sent or received so far.</param>
/// <param name="Total">The total number of bytes, when known (a seekable upload stream, a download with <c>Content-Length</c>).</param>
public readonly record struct ApiTransferProgress(long Transferred, long? Total)
{
    /// <summary>The completed fraction between 0 and 1, when the total is known.</summary>
    public double? Fraction => Total is > 0 ? Math.Min(1d, (double)Transferred / Total.Value) : null;
}

/// <summary>A file (or any binary payload) sent as a request body or a multipart part.</summary>
public sealed class FileContent
{
    /// <summary>
    /// Creates file content from a stream. The stream is read when the request is sent and disposed with the request.
    /// A seekable stream is read from its current position and rewound for retries.
    /// </summary>
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

    /// <summary>
    /// Receives upload progress while the content is sent (after each buffer). In Blazor WebAssembly the browser sends
    /// the body, so progress reflects copying it to the browser rather than network transfer.
    /// </summary>
    public IProgress<ApiTransferProgress>? Progress { get; init; }

    /// <summary>Creates file content from bytes.</summary>
    public static FileContent FromBytes(byte[] bytes, string? fileName = null, string? contentType = null) =>
        new(new MemoryStream(bytes ?? throw new ArgumentNullException(nameof(bytes)), writable: false), fileName, contentType);

    /// <summary>Creates the HTTP content.</summary>
    public HttpContent ToHttpContent(string? defaultContentType = null)
    {
        HttpContent content = Progress is null ? new StreamContent(Content) : new ProgressContent(Content, Progress);
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
        FileName = SafeFileName(response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"') ??
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
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

    /// <summary>
    /// Reduces a server-supplied name to its last segment, so <c>../../etc/passwd</c> becomes <c>passwd</c> and a caller
    /// combining it with a directory cannot write elsewhere.
    /// </summary>
    private static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var last = name!.Replace('\\', '/').Split('/').Last().Trim();
        // A drive-relative name (C:evil.exe) is rooted on Windows, and Path.Combine would discard the directory.
        return last is "" or "." or ".." || last.IndexOf(':') >= 0 || last.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : last;
    }

    /// <summary>
    /// The file name from <c>Content-Disposition</c>, when present, without directory segments (server text: still
    /// validate it before using it as a path).
    /// </summary>
    public string? FileName { get; }

    /// <summary>The response headers.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <summary>Reads the whole body into memory.</summary>
    public Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default) => ReadAllBytesAsync(null, cancellationToken);

    /// <summary>Reads the whole body into memory, reporting progress.</summary>
    public async Task<byte[]> ReadAllBytesAsync(IProgress<ApiTransferProgress>? progress, CancellationToken cancellationToken = default)
    {
        using var buffer = ContentLength is > 0 and <= int.MaxValue ? new MemoryStream((int)ContentLength.Value) : new MemoryStream();
        await CopyToAsync(buffer, progress, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Copies the body to a stream (for example a file), reporting progress after each buffer.</summary>
    public async Task CopyToAsync(Stream destination, IProgress<ApiTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (progress is null)
        {
            await Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ProgressContent.CopyAsync(Content, destination, progress, ContentLength, cancellationToken).ConfigureAwait(false);
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

/// <summary>Stream content that reports progress while it is sent.</summary>
internal sealed class ProgressContent : HttpContent
{
    private const int BufferSize = 81920;
    private readonly Stream _stream;
    private readonly IProgress<ApiTransferProgress> _progress;
    private readonly long _start;

    public ProgressContent(Stream stream, IProgress<ApiTransferProgress> progress)
    {
        _stream = stream;
        _progress = progress;
        _start = stream.CanSeek ? stream.Position : 0;
    }

    public static async Task CopyAsync(Stream source, Stream destination, IProgress<ApiTransferProgress> progress, long? total, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long transferred = 0;
        progress.Report(new ApiTransferProgress(0, total));
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            transferred += read;
            progress.Report(new ApiTransferProgress(transferred, total));
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        if (_stream.CanSeek)
        {
            _stream.Position = _start; // Sent again by a retry or an authentication handler.
        }

        return CopyAsync(_stream, stream, _progress, _stream.CanSeek ? _stream.Length - _start : null, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _stream.CanSeek ? _stream.Length - _start : 0;
        return _stream.CanSeek;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}
