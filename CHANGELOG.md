# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0-preview] - Unreleased

First preview. Packages: `BlazorApi.OpenApi`, `.CodeGeneration`, `.Runtime`, `.SourceGenerator`, `.Cli`,
`.Testing`, `.Blazor`. Libraries target .NET 8 and .NET 10; the engine and the source generator
target .NET Standard 2.0.

### Added

- **OpenAPI engine** without third-party dependencies: JSON and YAML 1.2 parsers with source positions and safety
  limits; OpenAPI 3.0 and 3.1 (3.2 read as 3.1 with a warning, Swagger 2.0 rejected with guidance); local and
  external file references with a restrictive reference policy and reference root; semantic validation; a
  normalized API model; stable `BAPIxxxx` diagnostics with JSON pointers and per-id severity overrides.
- **Code generation**: one interface and class per client (or per tag), `XxxAsync` and `XxxResultAsync` methods per
  operation, models with `required` members, contract-driven nullability, `Optional<T>` for merge-patch bodies,
  DataAnnotations and XML documentation; `allOf` inheritance, discriminated hierarchies with generated converters
  and `Unrecognized*` types, `oneOf`/`anyOf` unions, strict enum converters; operation and tag filters and
  unused-model pruning; deterministic output.
- **Runtime**: `ApiConnection` with OpenAPI parameter serialization (path, query, header, cookie styles), JSON,
  text, form, multipart and binary bodies (`FileContent`, streamed `FileResponse`); `ApiResult`/`ApiResult<T>`,
  `ApiError` with typed error bodies and problem details, the `ApiException` family; timeouts, default headers and
  request/response hooks; `IHttpClientFactory` registration.
- **Authentication**: scheme-aware bearer (with caching and refresh on 401), API key, Basic and OAuth 2.0 client
  credentials handlers, with `AddBearerToken`, `AddApiKey` and `AddBasicAuthentication`.
- **Pagination**: detection of cursor, page, offset and `Link` header pagination, `x-pagination`, generated page and
  item walkers.
- **Testing** (`GenerateMocks`): generated client mocks on `ApiMock`, model fixtures, `MockApiHandler` with request
  recording and verification.
- **Source generator**: `OpenApiContract` and `OpenApiReference` MSBuild items with `Namespace`, `ClientName`,
  `Options` and `Config` metadata; diagnostics with locations in the IDE; per-contract incremental pipelines.
- **CLI** (`api-gen`): `generate` (configuration files with several contracts, manifest-based stale file removal,
  `--dry-run`, `--check`), `validate`, `diff` (text, Markdown and JSON reports, `--fail-on`, `--ignore`) and
  `inspect`.
- **Contract compatibility analysis** (`ContractComparer`) with request/response-aware change classification.
- **Blazor components**: `ApiResultView`, `ApiErrorView` and `ApiValidationErrors` for server validation errors in
  `EditForm`.
- Demo projects (Blazor Web App, console client, tests), documentation and a BenchmarkDotNet suite.
- **Streamed responses**: `text/event-stream` (server-sent events) and JSON sequences (`application/jsonl`,
  `application/x-ndjson`, `application/json-seq`) become `ApiStream<T>` (`IAsyncEnumerable<T>`) with
  `ServerSentEvent<T>` items; OpenAPI 3.2 `itemSchema` is read.
- **Per-call options**: generated `WithOptions(...)` on clients and interfaces, `ApiClientOptions.Clone()`.
- **Servers**: `{Client}.Servers` (`ApiServer` with variable resolution and `enum` checks); operation- and path-level
  servers are honored, with `ApiClientOptions.OperationBaseAddresses` overrides.
- **Retries**: `ApiRetryHandler`/`AddApiRetries()` retry idempotent requests (or requests with an `Idempotency-Key`)
  on transient statuses and connection failures, honoring `Retry-After` and `retry-after-ms`; optional generated
  idempotency keys.
- **Telemetry**: `ActivitySource` and `Meter` named `BlazorApi` with a span per operation and the
  `blazorapi.client.operation.duration` histogram (`url.template`, operation id, status, `error.type`).
- **Forward-compatible enums**: `unknownEnumValues=unrecognized` adds an `Unrecognized` member for values the
  contract does not list.
- **Implicit discriminators**: object variants of a `oneOf`/`anyOf` without a `discriminator` are told apart by a
  property with a different constant value in each (OpenAI's `type`, Stripe's `object`), also next to variants of
  other JSON kinds (expandable fields); inline variants are named after their `title`.
- **Mock validation**: `api-gen mock` checks credentials (401), content types (415), parameters and JSON bodies
  against the contract (400 with an RFC 9457 `errors` list); `--no-validate` turns it off.
- **Caching**: `ApiCacheOptions.UseFreshness` serves fresh responses (`max-age`, `Expires`) without a request; a
  successful unsafe request drops the cached responses of its URL.
- **AOT**: `generateJsonContext=true` emits a `JsonSerializerContext` used by default, with metadata written
  directly through `JsonMetadataServices`, so it works with the source generator as well as the CLI;
  `ApiJson.CreateOptions` accepts resolvers and the runtime ships metadata for its own types.
- **CLI**: contracts can be http(s) URLs (same-origin references are downloaded); `validate` reads `blazorapi.json`
  and `--config`.

- **Overlays**: OpenAPI Overlay 1.0-1.2 (`update`, `copy`, `remove`, reusable actions) with an RFC 9535 JSONPath
  implementation (`JsonPath`), from `OpenApiLoadOptions.Overlays`, the `overlays` configuration key, `--overlay`, and
  the `Overlays` metadata or `OpenApiOverlay` items; diagnostics BAPI7001-7005.
- **OpenAPI 3.2**: `additionalOperations` (custom HTTP methods) and form-encoded `querystring` parameters.
- **Durations**: `durationType=timeSpan` maps `format: duration` to `TimeSpan` (ISO 8601 via `ApiDuration` and
  `ApiDurationJsonConverter`, registered by `ApiJson.CreateOptions`).
- **CLI**: `generate --watch` regenerates on contract, overlay and configuration changes.
- **Blazor**: `ApiStreamView<TItem>` renders streamed responses and asynchronous sequences (page walkers) as they
  arrive, with optional paging ("load more").

- **Responses**: other success bodies are read into `ApiResult.Content` (`TryGetContent`, `ApiResults.SuccessWithContent`,
  `ApiResponseHandler.SuccessAs`); BAPI6001 is now info. Typed `{Operation}ResponseHeaders` classes
  (`generateResponseHeaders`) with `ApiHeaderValues` parsers.
- **Transfers**: `FileContent.Progress`, `FileResponse.CopyToAsync(destination, progress)` and
  `ReadAllBytesAsync(progress)` report `ApiTransferProgress`.
- **Server-sent events**: `ApiEventSource.Reconnecting` reopens broken streams with `Last-Event-ID` and the server's
  `retry` delay.
- **Logging**: `AddApiLogging()` / `ApiLoggingHandler` log operations by template and warn once about `Deprecation` and
  `Sunset` headers; spans get `blazorapi.deprecated`.
- **Mock server**: `api-gen mock` serves examples (or values synthesized by `OpenApiExamples`) with `Prefer` response
  selection and open CORS.

- **Caching**: `AddApiCaching()` / `ApiCacheHandler` revalidate GET responses with `If-None-Match` and
  `If-Modified-Since` and answer 304s from a per-user, `Vary`-aware in-memory cache (`ApiResponseCache`).

- **Response bodies**: operations with different success body types get `{Operation}ResponseBodies`, a typed view
  with one property per status.
- **Type mappings**: `typeMappings` maps component schemas or formats to existing C# types (shared models, domain
  types); mapped schemas are not generated.
- **Demo**: the Blazor app uses retries, caching and logging, shows a pet's `ETag` and cache revalidation, and a live
  activity feed (server-sent events with `ApiStreamView`); the console client shows typed headers and streaming.

- **CI**: a native AOT smoke test publishes a client built by the source generator (`GenerateJsonContext=true`) with
  `PublishAot` and warnings as errors, and runs it against `api-gen mock` with reflection-based JSON disabled.

### Fixed

- Found by generating real-world contracts (GitHub, Stripe, OpenAI, Kubernetes, Twilio, Asana, Jira, Box): paths that
  differ only in variable names but declare different methods (GitHub, Jira) are now a warning (BAPI3019) instead of
  an error; `allOf: [$ref, {description}]` maps to the referenced type instead of an empty union or a subclass;
  ambiguous unions with value-type variants compile; `[Obsolete]` is no longer put on required members (CS9042); fixtures compile when a
  model is named like another model's fixture method (`CreateEvalItem`).
- Server-sent events: `id:` and `retry:` fields in a block without data now apply (WHATWG), and an empty `id:`
  resets the last event id, so `ApiEventSource.Reconnecting` sends no `Last-Event-ID`.
- Sending an `Unrecognized` enum member fails with a message that explains it (string enums gave no message).
- A model whose allOf base is mapped with `typeMappings` now declares the inherited properties (they were dropped,
  and the JSON context crashed the generator).
- The generated JSON context honors `NumberHandling` and `RespectNullableAnnotations` from the options and writes
  properties in the same order as reflection.
- Issues found by reviews of the new code: resilience values out of range (`retry-after-ms: Infinity`, huge SSE
  `retry`, ISO durations beyond `TimeSpan`) threw instead of being ignored or rejected; `MaxErrorBodyLength =
  int.MaxValue` overflowed; a UTF-8 BOM dropped the first server-sent event; streamed lines had no size limit;
  `ApiStream.From(IAsyncEnumerable)` blocked on disposal; `Content-Disposition` names kept directory segments;
  server variables could change the host; integer enums wrote the `Unrecognized` sentinel; absolute operation
  servers kept the document's relative base path; header classes with colliding names did not compile; the JSON
  context dropped user types named like runtime types; contracts sharing a file name broke the source generator;
  overlay names were matched by suffix; JSONPath regexes could escape their anchoring or use non-I-Regexp syntax, lone
  surrogates failed comparisons, logical functions were accepted as values, and overlays could grow documents
  without bound; example synthesis overflowed on extreme bounds, ignored `exclusiveMaximum` and could change the
  document; remote contracts followed redirects to other hosts; the mock server had no Host check, timeouts or limits.
- A second review round fixed: example values outside `exclusiveMinimum`/`maximum` bounds (and an overflow); drive-relative
  download file names (`C:evil.exe`); the cache losing a URL's `Vary` entry when a varying response was replaced;
  generator crashes or uncompilable code with mapped types in discriminated unions, form bodies and query arrays;
  `{Operation}ResponseBodies` reading a different media type than the client and setting `default`/range properties
  for declared statuses; header variable collisions; contracts whose names only collide after sanitizing or that share
  a parent folder name; a mock server stopping on a connection reset; and the demo reading oversized uploads.
- Values typed as an intermediate class of a discriminated hierarchy (`Car` with a `SportsCar` subtype) were read as
  that class; intermediate classes now get their own converter and dispatch to their subtypes.

- Exceptions of resilience pipelines (Polly timeouts, open circuits, rate limiters) escaped `XxxResultAsync`; they
  are now `Timeout` and `Network` errors.
- `application/x-ndjson` bodies were read as a single JSON document.
- The source generator package now declares its dependency on `BlazorApi.Runtime`.
- Generated responses carry their request (`HttpResponseMessage.RequestMessage`) even when a handler created them.

### Known limitations

- No BlazorApi package has been published yet.
- Trimmed and native AOT applications that use the source generator (not the CLI) must supply a
  `JsonSerializerContext` for the generated types.
- Only the first success body of an operation is the typed value (others are in `ApiResult.Content`);
  `format: time` and `duration` map to `string` by default.
- See `IMPLEMENTATION-STATUS.md` for the complete list.
