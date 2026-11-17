using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace BlazorApi.Runtime.Http;

/// <summary>Settings of <see cref="ApiRetryHandler"/>.</summary>
public sealed class ApiRetryOptions
{
    /// <summary>The number of retries after the first attempt. Default: 2. Zero disables retrying.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>The delay before the first retry; it doubles for each further retry (with jitter). Default: 0.5 s.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest computed backoff delay. Default: 8 s.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The longest delay a server may request with <c>Retry-After</c> (or <c>retry-after-ms</c>). A response asking
    /// for a longer wait is returned without retrying. Default: 60 s.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The status codes that are retried. Default: 408, 429, 500, 502, 503 and 504.</summary>
    public ISet<int> StatusCodes { get; } = new HashSet<int> { 408, 429, 500, 502, 503, 504 };

    /// <summary>Whether connection failures (<see cref="HttpRequestException"/>) are retried. Default: true.</summary>
    public bool RetryNetworkErrors { get; set; } = true;

    /// <summary>
    /// The header that makes a non-idempotent request (POST, PATCH) safe to retry. Requests that carry it are retried
    /// with the same value. Default: <c>Idempotency-Key</c> (IETF draft-ietf-httpapi-idempotency-key-header).
    /// </summary>
    public string IdempotencyKeyHeader { get; set; } = "Idempotency-Key";

    /// <summary>
    /// Whether POST and PATCH requests without an idempotency key get a new random one (so they can be retried and
    /// the server can deduplicate them). Only enable it for APIs that support idempotency keys. Default: false.
    /// </summary>
    public bool AddIdempotencyKeys { get; set; }

    /// <summary>
    /// The largest request body buffered in memory so it can be sent again. Requests with a larger or unknown-length
    /// streamed body are sent once. Bodies that can be re-serialized (JSON, text, forms, byte arrays) are never
    /// buffered. Default: 4 MiB.
    /// </summary>
    public long MaxBufferedContentLength { get; set; } = 4 * 1024 * 1024;

    /// <summary>The clock used for delays (for tests). Default: the system clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Retries transient failures safely: only idempotent requests (GET, HEAD, OPTIONS, TRACE, PUT, DELETE, QUERY) or
/// requests carrying an idempotency key, on the configured status codes or connection failures, with exponential
/// backoff and jitter, honoring <c>Retry-After</c> (RFC 9110) and <c>retry-after-ms</c>. Caller cancellation is never
/// retried. Add it with <c>AddApiRetries()</c> on the client's <c>IHttpClientBuilder</c>; for circuit breakers,
/// hedging or rate limiting use Microsoft.Extensions.Http.Resilience instead (not both).
/// </summary>
public sealed class ApiRetryHandler : DelegatingHandler
{
    /// <summary>The request option that holds the number of the current retry (0 for the first attempt).</summary>
    public static readonly HttpRequestOptionsKey<int> AttemptKey = new("BlazorApi.RetryAttempt");

    private readonly ApiRetryOptions _options;

    /// <summary>Creates the handler.</summary>
    public ApiRetryHandler(ApiRetryOptions? options = null) => _options = options ?? new ApiRetryOptions();

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (_options.AddIdempotencyKeys && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch) &&
            !request.Headers.Contains(_options.IdempotencyKeyHeader))
        {
            request.Headers.TryAddWithoutValidation(_options.IdempotencyKeyHeader, Guid.NewGuid().ToString("N"));
        }

        var canRetry = _options.MaxRetries > 0 && IsRetryable(request) &&
            await PrepareContentAsync(request.Content, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            request.Options.Set(AttemptKey, attempt);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (canRetry && _options.RetryNetworkErrors && attempt < _options.MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                await DelayAsync(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!canRetry || attempt >= _options.MaxRetries || !_options.StatusCodes.Contains((int)response.StatusCode))
            {
                return response;
            }

            var retryAfter = RetryAfter(response);
            if (retryAfter > _options.MaxRetryAfter)
            {
                return response;
            }

            response.Dispose();
            await DelayAsync(retryAfter ?? Backoff(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsRetryable(HttpRequestMessage request)
    {
        var method = request.Method.Method;
        return method is "GET" or "HEAD" or "OPTIONS" or "TRACE" or "PUT" or "DELETE" or "QUERY" ||
            request.Headers.Contains(_options.IdempotencyKeyHeader);
    }

    /// <summary>Makes the body replayable: re-serializable content as is, small streamed content buffered.</summary>
    private async Task<bool> PrepareContentAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        switch (content)
        {
            case null:
            case ByteArrayContent: // StringContent, FormUrlEncodedContent, ReadOnlyMemoryContent-like bodies
            case JsonContent:
                return true;
            case MultipartContent multipart:
                foreach (var part in multipart)
                {
                    if (!await PrepareContentAsync(part, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                return true;
        }

        if (content.Headers.ContentLength is not { } length || length > _options.MaxBufferedContentLength)
        {
            return false;
        }

#if NET9_0_OR_GREATER
        await content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
#else
        await content.LoadIntoBufferAsync().ConfigureAwait(false);
#endif
        return true;
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var capped = Math.Min(exponential, _options.MaxDelay.TotalMilliseconds);
#pragma warning disable CA5394 // Jitter does not need a cryptographic random number generator.
        var jittered = capped * (0.5 + (Random.Shared.NextDouble() * 0.5));
#pragma warning restore CA5394
        return TimeSpan.FromMilliseconds(jittered);
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _options.TimeProvider, cancellationToken);

    /// <summary>The delay a response asks for: <c>retry-after-ms</c>, else <c>Retry-After</c> seconds or date.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds >= 0)
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }

            if (retryAfter.Date is { } date)
            {
                var wait = date - _options.TimeProvider.GetUtcNow();
                return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            }
        }

        return null;
    }
}
