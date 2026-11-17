using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace BlazorApi.Runtime.Http;

/// <summary>Settings of <see cref="ApiCacheHandler"/>.</summary>
public sealed class ApiCacheOptions
{
    /// <summary>The number of responses kept (least recently used ones are dropped). Default: 256.</summary>
    public int MaxEntries { get; set; } = 256;

    /// <summary>The largest response body that is cached. Default: 1 MiB.</summary>
    public long MaxBodyLength { get; set; } = 1024 * 1024;

    /// <summary>The total size of the cached bodies (least recently used ones are dropped). Default: 32 MiB.</summary>
    public long MaxTotalBytes { get; set; } = 32L * 1024 * 1024;
}

/// <summary>
/// Revalidates GET responses instead of downloading them again (RFC 9110 conditional requests): a 200 response with an
/// <c>ETag</c> or <c>Last-Modified</c> is kept in memory, the next GET of the same URL sends <c>If-None-Match</c> /
/// <c>If-Modified-Since</c>, and a <c>304 Not Modified</c> answer is turned back into the cached 200 response.
/// </summary>
/// <remarks>
/// Cache keys are the URL plus a hash of every request header that can identify a caller (<c>Authorization</c>,
/// <c>Cookie</c>, API key and tenant headers: all headers except per-request ones such as trace and correlation ids),
/// plus the headers a response names in <c>Vary</c>. Cookies that a <c>CookieContainer</c> of the primary handler adds
/// after this handler are not visible to it: do not share such a client between users. Responses marked
/// <c>Cache-Control: no-store</c>, <c>Vary: *</c>, event streams and bodies over the size limits are not cached.
/// Requests that carry their own conditional headers pass through. Add it with <c>AddApiCaching()</c>.
/// </remarks>
public sealed class ApiCacheHandler : DelegatingHandler
{
    /// <summary>The response header that marks a response served from the cache after a 304.</summary>
    public const string CacheHeader = "X-BlazorApi-Cache";

    /// <summary>Marks conditional headers this handler added, so a retried request is not mistaken for the caller's own.</summary>
    private static readonly HttpRequestOptionsKey<bool> AddedConditionKey = new("BlazorApi.CacheAddedCondition");

    /// <summary>Request headers that change per request and so must not split cache keys.</summary>
    private static readonly HashSet<string> PerRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context", "X-Request-Id", "X-Correlation-Id",
        "Idempotency-Key", "If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since", "If-Range",
    };

    private readonly ApiResponseCache _cache;

    /// <summary>Creates the handler with its own cache.</summary>
    public ApiCacheHandler(ApiCacheOptions? options = null)
        : this(new ApiResponseCache(options))
    {
    }

    /// <summary>
    /// Creates the handler with a cache shared by several handler instances (<c>IHttpClientFactory</c> recreates
    /// handlers periodically; the cache outlives them).
    /// </summary>
    public ApiCacheHandler(ApiResponseCache cache) => _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    /// <summary>The cache.</summary>
    public ApiResponseCache Cache => _cache;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        // A request seen again (an outer retry handler): drop the conditions added last time before deciding.
        if (request.Options.TryGetValue(AddedConditionKey, out var added) && added)
        {
            request.Headers.IfNoneMatch.Clear();
            request.Headers.IfModifiedSince = null;
            request.Options.Set(AddedConditionKey, false);
        }

        if (request.Method != HttpMethod.Get || request.RequestUri is null ||
            request.Headers.IfNoneMatch.Count > 0 || request.Headers.IfModifiedSince is not null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Keys come from the request as the caller built it, before inner handlers change headers.
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var baseKey = BaseKey(request.RequestUri, headers);
        var generation = _cache.Generation;
        var cached = _cache.Find(baseKey, headers);
        if (cached is not null)
        {
            if (cached.ETag is { } etag)
            {
                request.Headers.IfNoneMatch.Add(etag);
            }
            else
            {
                request.Headers.IfModifiedSince = cached.LastModified;
            }

            request.Options.Set(AddedConditionKey, true);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            response.Dispose();
            return cached.ToResponse(request);
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            try
            {
                await StoreAsync(baseKey, headers, generation, response, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        return response;
    }

    private async Task StoreAsync(string baseKey, Dictionary<string, string> requestHeaders, long generation, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var options = _cache.Options;
        var etag = response.Headers.ETag;
        var lastModified = response.Content.Headers.LastModified;
        var vary = response.Headers.Vary.ToArray();
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var length = response.Content.Headers.ContentLength;
        if ((etag is null && lastModified is null) || response.Headers.CacheControl?.NoStore == true || vary.Contains("*") ||
            length > options.MaxBodyLength || mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        byte[] body;
        if (length is not null)
        {
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Unknown length (chunked JSON): read at most the limit; a larger body is handed back unread.
            var original = response.Content;
            var stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while (buffer.Length <= options.MaxBodyLength &&
                   (read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length > options.MaxBodyLength)
            {
                buffer.Position = 0;
                response.Content = Replace(original, new PrefixedStream(buffer, stream));
                return;
            }

            body = buffer.ToArray();
            response.Content = Replace(original, new MemoryStream(body, writable: false));
            original.Dispose();
        }

        if (body.LongLength > options.MaxBodyLength)
        {
            return;
        }

        var entry = new Entry(baseKey, etag, lastModified, body,
            response.Headers.Select(h => new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray())).ToArray(),
            response.Content.Headers.Select(h => new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray())).ToArray(),
            response.Version);
        _cache.Store(baseKey, FullKey(baseKey, requestHeaders, vary), vary, entry, generation);
    }

    private static StreamContent Replace(HttpContent original, Stream body)
    {
        var content = new StreamContent(body);
        foreach (var header in original.Headers)
        {
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return content;
    }

    /// <summary>The URL and a hash of the caller-identifying headers, so cached responses are not shared between callers.</summary>
    private static string BaseKey(Uri uri, Dictionary<string, string> headers)
    {
        var identity = new StringBuilder();
        foreach (var header in headers.Where(h => !PerRequestHeaders.Contains(h.Key)).OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            identity.Append(header.Key.ToLowerInvariant()).Append(':').Append(header.Value).Append('\n');
        }

        var hash = identity.Length == 0 ? "-" : Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
        return uri.AbsoluteUri + "\n" + hash;
    }

    internal static string FullKey(string baseKey, Dictionary<string, string> headers, IReadOnlyList<string> vary)
    {
        if (vary.Count == 0)
        {
            return baseKey;
        }

        var key = new StringBuilder(baseKey);
        foreach (var name in vary.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            key.Append('\n').Append(name.ToLowerInvariant()).Append('=');
            if (headers.TryGetValue(name, out var value))
            {
                key.Append(value);
            }
        }

        return key.ToString();
    }

    internal sealed class Entry
    {
        public Entry(string baseKey, EntityTagHeaderValue? etag, DateTimeOffset? lastModified, byte[] body,
            KeyValuePair<string, string[]>[] headers, KeyValuePair<string, string[]>[] contentHeaders, Version version)
        {
            BaseKey = baseKey;
            ETag = etag;
            LastModified = lastModified;
            Body = body;
            Headers = headers;
            ContentHeaders = contentHeaders;
            Version = version;
        }

        public string BaseKey { get; }

        public EntityTagHeaderValue? ETag { get; }

        public DateTimeOffset? LastModified { get; }

        public byte[] Body { get; }

        public KeyValuePair<string, string[]>[] Headers { get; }

        public KeyValuePair<string, string[]>[] ContentHeaders { get; }

        public Version Version { get; }

        public HttpResponseMessage ToResponse(HttpRequestMessage request)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Version = Version, Content = new ByteArrayContent(Body) };
            foreach (var header in Headers)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var header in ContentHeaders)
            {
                response.Content.Headers.Remove(header.Key);
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Headers.TryAddWithoutValidation(CacheHeader, "revalidated");
            return response;
        }
    }

    /// <summary>A stream that returns buffered bytes, then the rest of the original stream.</summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly MemoryStream _prefix;
        private readonly Stream _rest;

        public PrefixedStream(MemoryStream prefix, Stream rest)
        {
            _prefix = prefix;
            _rest = rest;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _prefix.Read(buffer, offset, count);
            return read > 0 ? read : _rest.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = _prefix.Read(buffer.Span);
            return read > 0 ? read : await _rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
                _rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>The responses kept by <see cref="ApiCacheHandler"/>; one instance can serve several handler instances.</summary>
public sealed class ApiResponseCache
{
    private readonly LinkedList<(string Key, ApiCacheHandler.Entry Entry)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ApiCacheHandler.Entry Entry)>> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _vary = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _bytes;
    private long _generation;

    /// <summary>Creates an empty cache.</summary>
    public ApiResponseCache(ApiCacheOptions? options = null) => Options = options ?? new ApiCacheOptions();

    internal ApiCacheOptions Options { get; }

    /// <summary>Changes on <see cref="Clear"/>, so responses requested before a clear are not stored after it.</summary>
    internal long Generation => Interlocked.Read(ref _generation);

    /// <summary>The number of cached responses.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The total size of the cached bodies.</summary>
    public long Size
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Drops every cached response (for example after a user signs out).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            _entries.Clear();
            _order.Clear();
            _vary.Clear();
            _bytes = 0;
        }
    }

    internal ApiCacheHandler.Entry? Find(string baseKey, Dictionary<string, string> headers)
    {
        lock (_gate)
        {
            // The Vary headers of the URL's last response decide which request headers are part of the key.
            var key = ApiCacheHandler.FullKey(baseKey, headers, _vary.TryGetValue(baseKey, out var vary) ? vary : []);
            if (!_entries.TryGetValue(key, out var node))
            {
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Entry;
        }
    }

    internal void Store(string baseKey, string key, IReadOnlyList<string> vary, ApiCacheHandler.Entry entry, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return; // Cleared while the response was on its way.
            }

            _vary[baseKey] = vary;
            if (_entries.TryGetValue(key, out var existing))
            {
                Remove(existing);
            }

            _entries[key] = _order.AddFirst((key, entry));
            _bytes += entry.Body.LongLength;
            while ((_order.Count > Math.Max(Options.MaxEntries, 1) || _bytes > Options.MaxTotalBytes) && _order.Last is { } last)
            {
                Remove(last);
            }
        }
    }

    private void Remove(LinkedListNode<(string Key, ApiCacheHandler.Entry Entry)> node)
    {
        _order.Remove(node);
        _entries.Remove(node.Value.Key);
        _bytes -= node.Value.Entry.Body.LongLength;
        var baseKey = node.Value.Entry.BaseKey;
        if (!_order.Any(e => e.Entry.BaseKey == baseKey))
        {
            _vary.Remove(baseKey);
        }
    }
}
