using System.Diagnostics;
using BlazorApi.Runtime.Http;

namespace Petstore.BlazorApp.Services;

/// <summary>One HTTP exchange made by a generated client.</summary>
public sealed record ApiActivity(
    long Sequence,
    DateTimeOffset Time,
    string? OperationId,
    string Method,
    string Path,
    string Credentials,
    int? StatusCode,
    TimeSpan Duration,
    string? Failure);

/// <summary>
/// The most recent API calls, shown in the "API traffic" panel so you can see what the generated client sends: the
/// operation, the URL it built and the credentials the authentication handlers attached. The log is shared by everyone
/// using this demo instance.
/// </summary>
public sealed class ApiActivityLog
{
    private const int Capacity = 60;
    private readonly LinkedList<ApiActivity> _entries = new();
    private readonly Lock _lock = new();
    private long _sequence;

    public event Action? Changed;

    public IReadOnlyList<ApiActivity> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public void Add(Func<long, ApiActivity> create)
    {
        lock (_lock)
        {
            _entries.AddFirst(create(++_sequence));
            if (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }

        Changed?.Invoke();
    }
}

/// <summary>
/// Records each request in the <see cref="ApiActivityLog"/>. Added after the authentication handlers, it runs closest
/// to the network and sees the headers they attached. Secrets are never recorded, only which scheme was used.
/// </summary>
public sealed class ApiActivityHandler(ApiActivityLog log) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(ApiRequestOptionKeys.OperationId, out var operationId);
        var credentials = request.Headers.Authorization is { } auth ? auth.Scheme
            : request.Headers.Contains("X-API-Key") ? "API key"
            : "none";
        var started = Stopwatch.GetTimestamp();

        void Record(int? status, string? failure) => log.Add(sequence => new ApiActivity(
            sequence,
            DateTimeOffset.Now,
            operationId,
            request.Method.Method,
            request.RequestUri?.PathAndQuery ?? string.Empty,
            credentials,
            status,
            Stopwatch.GetElapsedTime(started),
            failure));

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            Record((int)response.StatusCode, null);
            return response;
        }
        catch (OperationCanceledException)
        {
            Record(null, "aborted (cancelled or timed out)");
            throw;
        }
        catch (HttpRequestException ex)
        {
            Record(null, ex.HttpRequestError == HttpRequestError.ConnectionError ? "connection refused" : "network error");
            throw;
        }
    }
}
