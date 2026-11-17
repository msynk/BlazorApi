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
- **Attributes** (spans and metric): `http.request.method`, `url.template` (the path template, such as
  `/pets/{petId}`), `blazorapi.operation.id`, `http.response.status_code` when a response arrived, and on failure
  `error.type`: the status code (`404`), `network`, `timeout`, `deserialization`, `cancelled` or an exception type.
  Failed calls set the span status to `Error`.

Only templates are recorded, never URLs, parameter values, headers or bodies, so credentials and personal data in
them do not reach telemetry backends. Streamed responses are measured to the response headers; reading the items is
not part of the span.
