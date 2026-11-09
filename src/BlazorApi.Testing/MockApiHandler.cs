using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BlazorApi.Runtime.Http;
using BlazorApi.Runtime.Json;

namespace BlazorApi.Testing;

/// <summary>A request received by <see cref="MockApiHandler"/>, with its body read into memory.</summary>
public sealed class RecordedApiRequest
{
    internal RecordedApiRequest(string? operationId, HttpMethod method, Uri? uri, IReadOnlyDictionary<string, string> headers, string? body)
    {
        OperationId = operationId;
        Method = method;
        Uri = uri;
        Headers = headers;
        Body = body;
    }

    /// <summary>The operation identifier set by the generated client, when present.</summary>
    public string? OperationId { get; }

    /// <summary>The HTTP method.</summary>
    public HttpMethod Method { get; }

    /// <summary>The request URI.</summary>
    public Uri? Uri { get; }

    /// <summary>Request and content headers (multiple values joined with commas).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The body text.</summary>
    public string? Body { get; }

    /// <summary>Reads the body as JSON.</summary>
    public JsonElement BodyJson => Body is null ? default : JsonDocument.Parse(Body).RootElement.Clone();
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers requests of generated clients by operation id, without a server.
/// Configure responses (values serialized as JSON, raw bodies, status codes, delays, exceptions) per operation;
/// every request is recorded for verification. Unconfigured operations answer 501.
/// </summary>
public sealed class MockApiHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _defaults = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedApiRequest> _requests = new();
    private readonly JsonSerializerOptions _json;

    /// <summary>Creates the handler.</summary>
    /// <param name="jsonOptions">The JSON options for serializing response values; the client defaults when omitted.</param>
    public MockApiHandler(JsonSerializerOptions? jsonOptions = null) => _json = jsonOptions ?? ApiJson.CreateOptions();

    /// <summary>Every request in order.</summary>
    public IReadOnlyList<RecordedApiRequest> Requests => _requests.ToArray();

    /// <summary>The requests of one operation.</summary>
    public IReadOnlyList<RecordedApiRequest> RequestsTo(string operationId) => _requests.Where(r => r.OperationId == operationId).ToArray();

    /// <summary>Answers every call of an operation with a JSON value.</summary>
    public MockApiHandler RespondJson<T>(string operationId, T value, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(operationId, (_, _) => Task.FromResult(Json(value, status)));

    /// <summary>Answers every call of an operation with a status and optional raw body.</summary>
    public MockApiHandler Respond(string operationId, HttpStatusCode status, string? body = null, string contentType = "application/json") =>
        Respond(operationId, (_, _) => Task.FromResult(Raw(status, body, contentType)));

    /// <summary>Answers every call of an operation with a custom function.</summary>
    public MockApiHandler Respond(string operationId, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _defaults[operationId] = respond ?? throw new ArgumentNullException(nameof(respond));
        return this;
    }

    /// <summary>Answers the next call of an operation (queued answers are used before the default answer).</summary>
    public MockApiHandler RespondOnceJson<T>(string operationId, T value, HttpStatusCode status = HttpStatusCode.OK)
    {
        _queued.GetOrAdd(operationId, _ => new()).Enqueue((_, _) => Task.FromResult(Json(value, status)));
        return this;
    }

    /// <summary>Makes the next call of an operation fail with a status and optional body.</summary>
    public MockApiHandler RespondOnce(string operationId, HttpStatusCode status, string? body = null, string contentType = "application/json")
    {
        _queued.GetOrAdd(operationId, _ => new()).Enqueue((_, _) => Task.FromResult(Raw(status, body, contentType)));
        return this;
    }

    /// <summary>Makes every call of an operation throw (for example <see cref="HttpRequestException"/> to simulate a network failure).</summary>
    public MockApiHandler Throw(string operationId, Exception exception) =>
        Respond(operationId, (_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Delays the answers of an operation (cancellation and client timeouts are honored).</summary>
    public MockApiHandler Delay(string operationId, TimeSpan delay)
    {
        var inner = _defaults.TryGetValue(operationId, out var existing) ? existing : null;
        return Respond(operationId, async (request, token) =>
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return inner is null ? Raw(HttpStatusCode.OK, null, "application/json") : await inner(request, token).ConfigureAwait(false);
        });
    }

    /// <summary>Verifies how often an operation was requested.</summary>
    public void Verify(string operationId, int times = 1)
    {
        var count = RequestsTo(operationId).Count;
        if (count != times)
        {
            throw new ApiMockVerificationException($"Expected {times} request(s) to '{operationId}' but found {count}.");
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var operationId = request.Options.TryGetValue(ApiRequestOptionKeys.OperationId, out var id) ? id : null;
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        _requests.Enqueue(new RecordedApiRequest(operationId, request.Method, request.RequestUri, headers, body));
        if (operationId is not null)
        {
            if (_queued.TryGetValue(operationId, out var queue) && queue.TryDequeue(out var once))
            {
                return await once(request, cancellationToken).ConfigureAwait(false);
            }

            if (_defaults.TryGetValue(operationId, out var respond))
            {
                return await respond(request, cancellationToken).ConfigureAwait(false);
            }
        }

        return Raw(HttpStatusCode.NotImplemented, $"No mock response is set up for '{operationId ?? request.RequestUri?.ToString()}'.", "text/plain");
    }

    private HttpResponseMessage Json<T>(T value, HttpStatusCode status) =>
        Raw(status, ApiJson.Serialize(value, _json), "application/json");

    private static HttpResponseMessage Raw(HttpStatusCode status, string? body, string contentType)
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        return response;
    }
}
