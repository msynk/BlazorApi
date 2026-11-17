# Resilience and telemetry

## Retries

`AddApiRetries` adds an `ApiRetryHandler` to a generated client's `IHttpClientBuilder`. It retries transient failures
without risking duplicate side effects:

```csharp
builder.Services.AddPetstoreClient(o => o.BaseAddress = new Uri("https://api.example.com/v1/"))
    .AddApiRetries(o => o.MaxRetries = 3)
    .AddBearerToken(sp => sp.GetRequiredService<IAccessTokenProvider>(), scheme: "bearerAuth");
```

| Behavior | Default (`ApiRetryOptions`) |
|---|---|
| Which requests | Idempotent methods (GET, HEAD, OPTIONS, TRACE, PUT, DELETE, QUERY), and POST/PATCH requests that carry an `Idempotency-Key` header (`IdempotencyKeyHeader`) |
| When | Status codes 408, 429, 500, 502, 503, 504 (`StatusCodes`) and connection failures (`RetryNetworkErrors`) |
| How often | `MaxRetries` = 2 retries after the first attempt |
| Delay | `retry-after-ms`, else `Retry-After` (seconds or HTTP date, RFC 9110), else exponential backoff from `BaseDelay` (0.5 s) up to `MaxDelay` (8 s) with jitter |
| Long waits | A `Retry-After` beyond `MaxRetryAfter` (60 s) is not waited for: the response is returned |
| Bodies | JSON, text, form and byte-array bodies are re-serialized; streamed bodies up to `MaxBufferedContentLength` (4 MiB) are buffered; larger streamed bodies are sent once |

Caller cancellation is never retried. `AddIdempotencyKeys = true` gives POST and PATCH requests without a key a new
random `Idempotency-Key` (reused by every retry of that request), which makes them retryable; enable it only for APIs
that deduplicate by that header (draft-ietf-httpapi-idempotency-key-header). A caller can also supply a key per call:

```csharp
await api.WithOptions(o => o.DefaultHeaders["Idempotency-Key"] = orderId).PlaceOrderAsync(order);
```

The handler sets `ApiRetryHandler.AttemptKey` (0 for the first attempt) in `HttpRequestMessage.Options` for inner
handlers. Add it before authentication handlers, so a retry passes through them again.

### Microsoft.Extensions.Http.Resilience

For circuit breakers, hedging, rate limiting or attempt timeouts, use `AddStandardResilienceHandler()` (or a custom
pipeline) instead of `AddApiRetries`; do not combine both, or retries multiply. Exceptions of the pipeline are
results like any other failure: a Polly timeout becomes an `ApiErrorKind.Timeout` error and other rejections (open
circuit, rate limiter) an `ApiErrorKind.Network` error, so `XxxResultAsync` does not throw for them. The standard
handler retries POST as well; call `DisableForUnsafeHttpMethods()` on its retry options to keep requests without
idempotency keys from being sent twice.

## Conditional requests

`AddApiCaching()` adds an `ApiCacheHandler` that revalidates GET responses instead of downloading them again
(RFC 9110): a 200 response with an `ETag` or `Last-Modified` is kept in memory, the next GET of the same URL sends
`If-None-Match` or `If-Modified-Since`, and a `304 Not Modified` is answered with the cached response (marked with the
header `X-BlazorApi-Cache: revalidated`).

```csharp
builder.Services.AddPetstoreClient()
    .AddBearerToken(...)
    .AddApiCaching(o => o.MaxEntries = 500);   // after authentication: credentials are part of the key
```

- Keys are the URL, a hash of every request header that can identify a caller (`Authorization`, `Cookie`, API keys,
  tenant headers: all headers except per-request ones such as `traceparent` and correlation ids) and the headers a
  response names in `Vary`, so callers do not receive each other's responses. Cookies that a `CookieContainer` of the
  primary handler adds are invisible to the cache: do not share such a client between users.
- `Cache-Control: no-store`, `Vary: *`, event streams and bodies over `MaxBodyLength` (1 MiB) are not cached; a body
  of unknown length is read up to the limit and handed back intact when larger. `MaxEntries` (256) and
  `MaxTotalBytes` (32 MiB) bound the memory.
- It works with retry handlers in either order: conditions it added are removed and re-added when a request is sent
  again.
- The cache (`ApiResponseCache`) is shared by the handler instances `IHttpClientFactory` recreates; `Clear()` empties
  it (for example on sign-out). Requests that set their own conditional headers pass through.
- Browsers already cache for Blazor WebAssembly; the handler matters most for server and desktop apps.

## Logging

`AddApiLogging()` adds an `ApiLoggingHandler` that logs each request of a generated client under the category
`BlazorApi`, with the operation's semantics instead of its URL:

```csharp
builder.Services.AddPetstoreClient().AddApiRetries().AddApiLogging();
```

| Event | Level | Message |
|---|---|---|
| 1 `ApiOperationCompleted` | Debug | `getPet (GET /pets/{petId}) responded 200 in 12.3 ms` |
| 2 `ApiOperationErrorStatus` | Information | the same, for 4xx and 5xx statuses |
| 3 `ApiOperationFailed` | Warning | no response (connection failure), with the exception |
| 4 `ApiOperationDeprecated` | Warning | once per operation, when a response carries `Deprecation` (RFC 9745) or `Sunset` (RFC 8594), with deprecation/sunset `Link`s |

Only path templates are logged, never URLs, query strings, headers or bodies. Added after `AddApiRetries`, it logs
every attempt; added before, one entry per call. `IHttpClientFactory` keeps logging raw requests at its own categories.

## Telemetry

Generated clients are instrumented with an `ActivitySource` and a `Meter`, both named `BlazorApi`
(`ApiTelemetry.Name`). Nothing is recorded unless a listener subscribes:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("BlazorApi").AddHttpClientInstrumentation())
    .WithMetrics(m => m.AddMeter("BlazorApi").AddHttpClientInstrumentation());
```

- **Spans**: one per operation call, named after the operation id (`getPet`), of kind `Internal`. The HTTP client
  spans of the call (one per attempt, including retries) are its children, so a trace shows the API operation and
  each request it took.
- **Metric**: the histogram `blazorapi.client.operation.duration` (seconds), from the call to the result, including
  retries and reading the body.
- **Deprecation**: spans of responses with `Deprecation` or `Sunset` headers get `blazorapi.deprecated = true`.
- **Attributes** (spans and metric): `http.request.method`, `url.template` (the path template, such as
  `/pets/{petId}`), `blazorapi.operation.id`, `http.response.status_code` when a response arrived, and on failure
  `error.type`: the status code (`404`), `network`, `timeout`, `deserialization`, `cancelled` or an exception type.
  Failed calls set the span status to `Error`.

Only templates are recorded, never URLs, parameter values, headers or bodies, so credentials and personal data in
them do not reach telemetry backends. Streamed responses are measured to the response headers; reading the items is
not part of the span.
