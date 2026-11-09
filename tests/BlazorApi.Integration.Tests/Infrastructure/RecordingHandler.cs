using System.Net;
using System.Text;

namespace BlazorApi.Integration.Tests.Infrastructure;

/// <summary>A recorded request with its body read into memory.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, string? Body);

/// <summary>Answers requests with queued responses and records every request.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public RecordedRequest Last => Requests[^1];

    public RecordingHandler Respond(HttpStatusCode status, string? body = null, string contentType = "application/json")
    {
        _responses.Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            return Task.FromResult(response);
        });
        return this;
    }

    public RecordingHandler Respond(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _responses.Enqueue((request, _) => responder(request));
        return this;
    }

    public RecordingHandler Respond(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.MediaType, body));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No response queued for " + request.RequestUri);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await _responses.Dequeue()(request, cancellationToken);
    }
}
