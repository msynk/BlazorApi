using System.Text;
using System.Text.Json;
using BlazorApi.Runtime.Http;
using BlazorApi.Runtime.Json;

namespace BlazorApi.Runtime;

/// <summary>
/// A streamed response body: server-sent events (<c>text/event-stream</c>) or a JSON sequence (<c>application/jsonl</c>,
/// <c>application/x-ndjson</c>, <c>application/json-seq</c>), read item by item while the response arrives. Enumerate
/// it once with <c>await foreach</c>; the HTTP response is released when the enumeration ends or the stream is
/// disposed, so always dispose a stream you do not enumerate to the end (<c>await using</c>).
/// </summary>
/// <remarks>
/// Failures while reading items are thrown from the enumeration as the <see cref="ApiException"/> family:
/// <see cref="ApiNetworkException"/> when the connection breaks and <see cref="ApiDeserializationException"/> when an
/// item does not match its type. Caller cancellation throws <see cref="OperationCanceledException"/>.
/// </remarks>
/// <typeparam name="T">The item type.</typeparam>
public sealed class ApiStream<T> : IAsyncEnumerable<T>, IAsyncDisposable, IDisposable
{
    private readonly Func<CancellationToken, ValueTask<(bool HasItem, T Item)>> _next;
    private readonly IDisposable? _owner;
    private readonly Func<ValueTask>? _asyncOwner;
    private readonly string? _operationId;
    private int _enumerated;
    private int _disposed;

    internal ApiStream(Func<CancellationToken, ValueTask<(bool HasItem, T Item)>> next, IDisposable? owner, string? operationId)
    {
        _next = next;
        _owner = owner;
        _operationId = operationId;
    }

    internal ApiStream(Func<CancellationToken, ValueTask<(bool HasItem, T Item)>> next, Func<ValueTask> asyncOwner, string? operationId)
    {
        _next = next;
        _asyncOwner = asyncOwner;
        _operationId = operationId;
    }

    /// <inheritdoc />
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
        {
            throw new InvalidOperationException("A streamed response can be enumerated only once.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ApiStream<T>));
        }

        try
        {
            while (true)
            {
                var (hasItem, item) = await NextAsync(cancellationToken).ConfigureAwait(false);
                if (!hasItem)
                {
                    yield break;
                }

                yield return item;
            }
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads every remaining item into a list (for small streams and tests).</summary>
    public async Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<T>();
        await foreach (var item in this.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Releases the HTTP response. Asynchronous sources (<see cref="ApiStream.From{T}(IAsyncEnumerable{T})"/>) are
    /// released without waiting; prefer <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner?.Dispose();
            if (_asyncOwner is not null)
            {
                _ = _asyncOwner().AsTask(); // Never block: Blazor WebAssembly has a single thread.
            }
        }
    }

    /// <summary>Releases the HTTP response.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner?.Dispose();
            if (_asyncOwner is not null)
            {
                await _asyncOwner().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<(bool HasItem, T Item)> NextAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _next(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ApiError(ApiErrorKind.Deserialization, $"An item of the streamed response of '{_operationId}' could not be read as {typeof(T).Name}: {ex.Message}")
            {
                Exception = ex,
                OperationId = _operationId,
            }.ToException();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            throw new ApiError(ApiErrorKind.Network, $"The streamed response of '{_operationId}' failed: {ex.Message}")
            {
                Exception = ex,
                OperationId = _operationId,
            }.ToException();
        }
    }
}

/// <summary>Creates <see cref="ApiStream{T}"/> values (for mocks and tests) and reads streamed bodies.</summary>
public static class ApiStream
{
    /// <summary>A stream of the given items.</summary>
    public static ApiStream<T> From<T>(IEnumerable<T> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        IEnumerator<T>? enumerator = null;
        return new ApiStream<T>(
            _ =>
            {
                enumerator ??= items.GetEnumerator();
                return new ValueTask<(bool, T)>(enumerator.MoveNext() ? (true, enumerator.Current) : (false, default!));
            },
            new DisposeAction(() => enumerator?.Dispose()),
            null);
    }

    /// <summary>A stream of the given asynchronous items (for example with delays, to simulate a slow server).</summary>
    public static ApiStream<T> From<T>(IAsyncEnumerable<T> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        IAsyncEnumerator<T>? enumerator = null;
        return new ApiStream<T>(
            async cancellationToken =>
            {
                enumerator ??= items.GetAsyncEnumerator(cancellationToken);
                return await enumerator.MoveNextAsync().ConfigureAwait(false) ? (true, enumerator.Current) : (false, default!);
            },
            () => enumerator?.DisposeAsync() ?? default,
            null);
    }

    internal static ApiStream<ServerSentEvent<T>> EventStream<T>(HttpResponseMessage response, Stream stream, JsonSerializerOptions options)
    {
        // The event stream format is UTF-8; a leading byte order mark is skipped (WHATWG).
        var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var parser = new ServerSentEventParser(new LineReader(reader));
        return new ApiStream<ServerSentEvent<T>>(
            async cancellationToken =>
            {
                var parsed = await parser.ReadAsync(cancellationToken).ConfigureAwait(false);
                return parsed is { } e ? (true, new ServerSentEvent<T>(e.Data, e.EventType, e.Id, e.Retry, options)) : (false, null!);
            },
            new DisposeAction(() =>
            {
                reader.Dispose();
                response.Dispose();
            }),
            OperationId(response));
    }

    internal static ApiStream<T> JsonSequence<T>(HttpResponseMessage response, Stream stream, JsonSerializerOptions options)
    {
        var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var lines = new LineReader(reader);
        var typeInfo = ApiJson.TypeInfo<T>(options);
        var record = new StringBuilder();
        var sequence = false; // RFC 7464 (json-seq): records start with RS and may span lines.
        return new ApiStream<T>(
            async cancellationToken =>
            {
                while (true)
                {
                    var line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        if (record.Length > 0)
                        {
                            var last = record.ToString();
                            record.Clear();
                            return (true, JsonSerializer.Deserialize(last, typeInfo)!);
                        }

                        return (false, default!);
                    }

                    if (line.Length > 0 && line[0] == '\u001e')
                    {
                        sequence = true;
                        var previous = record.ToString();
                        record.Clear().Append(line, 1, line.Length - 1);
                        if (previous.Trim().Length > 0)
                        {
                            return (true, JsonSerializer.Deserialize(previous, typeInfo)!);
                        }

                        continue;
                    }

                    if (sequence)
                    {
                        record.Append('\n').Append(line);
                        if (record.Length > LineReader.MaxLength)
                        {
                            throw new JsonException($"A record of the streamed response is longer than {LineReader.MaxLength} characters.");
                        }

                        continue;
                    }

                    if (line.Trim().Length > 0)
                    {
                        return (true, JsonSerializer.Deserialize(line, typeInfo)!);
                    }
                }
            },
            new DisposeAction(() =>
            {
                reader.Dispose();
                response.Dispose();
            }),
            OperationId(response));
    }


    private static string? OperationId(HttpResponseMessage response) =>
        response.RequestMessage is { } request && request.Options.TryGetValue(ApiRequestOptionKeys.OperationId, out var id) ? id : null;

    private sealed class DisposeAction : IDisposable
    {
        private Action? _action;

        public DisposeAction(Action action) => _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>One server-sent event (WHATWG HTML, "Server-sent events").</summary>
/// <typeparam name="T">The type of the event data, from the OpenAPI document.</typeparam>
public sealed class ServerSentEvent<T>
{
    private readonly JsonSerializerOptions? _options;
    private T? _value;
    private bool _hasValue;

    /// <summary>Creates an event from its data text.</summary>
    /// <param name="data">The data (several <c>data:</c> lines joined with line feeds).</param>
    /// <param name="eventType">The event type; <c>message</c> when <see langword="null"/>.</param>
    /// <param name="id">The last event id.</param>
    /// <param name="retry">The reconnection time the server asked for.</param>
    /// <param name="options">The JSON options used to read the data; the default client options when <see langword="null"/>.</param>
    public ServerSentEvent(string data, string? eventType = null, string? id = null, TimeSpan? retry = null, JsonSerializerOptions? options = null)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        EventType = string.IsNullOrEmpty(eventType) ? "message" : eventType!;
        Id = id;
        Retry = retry;
        _options = options;
    }

    /// <summary>Creates an event with a typed value (for mocks and tests); <see cref="Data"/> is its JSON.</summary>
    public static ServerSentEvent<T> FromValue(T value, string? eventType = null, string? id = null, JsonSerializerOptions? options = null)
    {
        options ??= ApiJson.CreateOptions();
        var data = value is string text ? text : JsonSerializer.Serialize(value, ApiJson.TypeInfo<T>(options));
        return new ServerSentEvent<T>(data, eventType, id, null, options) { _value = value, _hasValue = true };
    }

    /// <summary>The event type (<c>event:</c> field); <c>message</c> by default.</summary>
    public string EventType { get; }

    /// <summary>The raw data text.</summary>
    public string Data { get; }

    /// <summary>The last event id (<c>id:</c> field), which a client sends as <c>Last-Event-ID</c> when it reconnects.</summary>
    public string? Id { get; }

    /// <summary>The reconnection time (<c>retry:</c> field), when the event sets one.</summary>
    public TimeSpan? Retry { get; }

    /// <summary>
    /// The data read as <typeparamref name="T"/>: the text itself for strings, otherwise JSON. Sentinel events that
    /// are not JSON (for example <c>[DONE]</c>) can be recognized with <see cref="Data"/> before calling this.
    /// </summary>
    /// <exception cref="JsonException">The data is not JSON of the declared type.</exception>
    public T GetValue()
    {
        if (!_hasValue)
        {
            _value = typeof(T) == typeof(string)
                ? (T)(object)Data
                : JsonSerializer.Deserialize(Data, ApiJson.TypeInfo<T>(_options ?? ApiJson.CreateOptions()))!;
            _hasValue = true;
        }

        return _value!;
    }

    /// <summary>Reads the data as <typeparamref name="T"/>; false when it is not JSON of that type.</summary>
    public bool TryGetValue(out T value)
    {
        try
        {
            value = GetValue();
            return true;
        }
        catch (JsonException)
        {
            value = default!;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{EventType}: {Data}";
}

/// <summary>Parses the <c>text/event-stream</c> format into events.</summary>
internal sealed class ServerSentEventParser
{
    private readonly LineReader _reader;
    private readonly StringBuilder _data = new();
    private string? _lastEventId;

    public ServerSentEventParser(LineReader reader) => _reader = reader;

    public readonly record struct Event(string Data, string? EventType, string? Id, TimeSpan? Retry);

    /// <summary>Reads the next event; <see langword="null"/> at the end of the stream (an incomplete event is discarded).</summary>
    public async ValueTask<Event?> ReadAsync(CancellationToken cancellationToken)
    {
        _data.Clear();
        var hasData = false;
        string? eventType = null;
        TimeSpan? retry = null;
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    return new Event(_data.ToString(), eventType, _lastEventId, retry);
                }

                // Blank line without data: the event is not dispatched (comments, keep-alives, a lone 'event:').
                eventType = null;
                retry = null;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line.Substring(0, colon);
            var value = colon < 0 ? string.Empty : line.Substring(colon + 1);
            if (value.Length > 0 && value[0] == ' ')
            {
                value = value.Substring(1);
            }

            switch (field)
            {
                case "data":
                    if (hasData)
                    {
                        _data.Append('\n');
                    }

                    _data.Append(value);
                    if (_data.Length > LineReader.MaxLength)
                    {
                        throw new JsonException($"An event of the streamed response is longer than {LineReader.MaxLength} characters.");
                    }

                    hasData = true;
                    break;
                case "event":
                    eventType = value;
                    break;
                case "id":
                    if (value.IndexOf('\0') < 0)
                    {
                        _lastEventId = value;
                    }

                    break;
                case "retry":
                    if (value.Length > 0 && value.All(c => c is >= '0' and <= '9') &&
                        long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds) &&
                        milliseconds <= (long)TimeSpan.MaxValue.TotalMilliseconds)
                    {
                        retry = TimeSpan.FromMilliseconds(milliseconds);
                    }

                    break;
            }
        }
    }
}

/// <summary>
/// Reads lines terminated by LF, CR or CRLF asynchronously (never with synchronous reads, which Blazor WebAssembly does
/// not allow), failing a line longer than <see cref="MaxLength"/> so a server cannot grow memory without bound.
/// </summary>
internal sealed class LineReader
{
    /// <summary>The longest line, event or record (16 Mi characters).</summary>
    public const int MaxLength = 16 * 1024 * 1024;

    private readonly TextReader _reader;
    private readonly char[] _buffer = new char[4096];
    private int _position;
    private int _length;
    private bool _skipLineFeed;
    private bool _ended;

    public LineReader(TextReader reader) => _reader = reader;

    /// <summary>The next line without its terminator; <see langword="null"/> at the end.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        StringBuilder? line = null;
        while (true)
        {
            if (_position == _length)
            {
                if (_ended)
                {
                    return line is { Length: > 0 } ? line.ToString() : null;
                }

                _length = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    _ended = true;
                    return line is { Length: > 0 } ? line.ToString() : null;
                }
            }

            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (_buffer[_position] == '\n')
                {
                    _position++;
                    continue;
                }
            }

            var start = _position;
            while (_position < _length && _buffer[_position] is not ('\n' or '\r'))
            {
                _position++;
            }

            line ??= new StringBuilder();
            line.Append(_buffer, start, _position - start);
            if (line.Length > MaxLength)
            {
                throw new System.Text.Json.JsonException($"A line of the streamed response is longer than {MaxLength} characters.");
            }

            if (_position < _length)
            {
                _skipLineFeed = _buffer[_position++] == '\r';
                return line.ToString();
            }
        }
    }
}
