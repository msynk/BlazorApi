# Implementation status

Tracking document for the BlazorApi roadmap (OpenAPI contract to typed .NET integration layer). Each milestone is
scoped to be reviewable on its own; the working tree has not been committed or split into pull requests yet.

## Milestones

| Milestone | Scope | Status |
|---|---|---|
| M01 | Solution layout, build settings, diagnostics core | Done |
| M02 | JSON/YAML parsing, OpenAPI 3.0/3.1 document model and reader | Done |
| M03 | Reference resolution (local, external files, policy) and normalized API model | Done |
| M04 | Semantic validation | Done |
| M05 | Configuration (`GeneratorOptions`) and naming | Done (JSON config file: see M20) |
| M06 | Primitive types and nullability | Done |
| M07 | DTO and schema composition generation (inheritance, discriminators, unions, enums) | Done |
| M08 | Serialization support (System.Text.Json converters, `Optional<T>`) | Done |
| M09 | HTTP runtime (`ApiConnection`, request builder, parameter serialization) | Done |
| M10 | Typed API clients | Done |
| M11 | Dependency injection (`IHttpClientFactory`) | Done |
| M12 | Typed errors (`ApiResult`, `ApiError`, `ApiException` family) | Done |
| M13 | Authentication (bearer, refresh, API key, Basic, client credentials) | Done (Blazor integration: see M23) |
| M14 | Query definitions and keys | Removed (see decision 14) |
| M15 | Query runtime integration (dotnet-query adapter `BlazorApi.DotNetQuery`) | Removed (see decision 14) |
| M16 | Mutation generation and invalidation (path relationships, `x-invalidates`, options) | Removed (see decision 14) |
| M17 | Pagination (cursor, page, offset, Link header, `x-pagination`), page walkers | Done |
| M18 | Mocking (`BlazorApi.Testing`: generated mocks, `MockApiHandler`) and fixtures | Done |
| M19 | Incremental source generator (`BlazorApi.SourceGenerator`) | Done |
| M20 | CLI (`api-gen generate/validate/diff/inspect`), JSON configuration, MSBuild items | Done |
| M21 | Contract compatibility analysis (`ContractComparer`) with policies and reports | Done |
| M22 | Advanced generation features | Partly: operation/tag filters, unused-model pruning, multi-contract projects, `x-pagination`, `x-enum-varnames`; custom templates deferred |
| M23 | Blazor integration (`BlazorApi.Blazor`: `ApiResultView`, `ApiErrorView`, `ApiValidationErrors`; auth registration helpers `AddBearerToken`/`AddApiKey`/`AddBasicAuthentication` in `BlazorApi.Runtime`) | Done |
| M24 | Documentation, demo, benchmarks, CI workflow, packaging | Done (not published) |
| M28 | Intermediate hierarchy classes dispatch to their subtypes (subtree converters with payload classes); conditional-request cache (`AddApiCaching`) | Done |
| M27 | Other success bodies (`ApiResult.Content`), typed response headers, transfer progress, reconnecting SSE, operation logging with deprecation warnings, `api-gen mock` with example synthesis | Done |
| M26 | Overlays (1.0-1.2, RFC 9535 JSONPath), OpenAPI 3.2 `additionalOperations` and `querystring`, `durationType=timeSpan`, `generate --watch`, Blazor `ApiStreamView` | Done |
| M25 | Gap closure after an ecosystem survey (see below): streaming, per-call options, servers, retries, telemetry, forward-compatible enums, JSON context, CLI URLs | Done |

## Tests executed

| Date | Scope | Result |
|---|---|---|
| 2026-11-09 | `dotnet test tests/BlazorApi.OpenApi.Tests` (net8.0, net10.0) | 104 passed |
| 2026-11-09 | `dotnet test BlazorApi.slnx` (OpenApi, CodeGeneration, Integration; net8.0 and net10.0) | 310 passed |
| 2026-11-09 | OpenApi (140), CodeGeneration (7), Integration (32), SourceGenerator (10), Cli (14), Blazor (8) per target framework (net8.0, net10.0) | 422 passed |
| 2026-11-09 | Full suite after security fixes: 430 tests (net8.0 + net10.0) + 5 demo tests | all passed |
| 2026-11-09 | `dotnet pack` of all 8 packages; a scratch net8.0 project consumed the SourceGenerator and Runtime packages from a local feed with warnings as errors | passed |
| 2026-11-09 | BenchmarkDotNet ShortRun (see docs/performance.md) | recorded |
| 2026-11-09 | After documentation-review fixes: 440 tests (net8.0 + net10.0) + 5 demo tests | all passed |
| 2026-11-09 | demo/BlazorApi.Demo.Tests (5); BlazorApi.Demo.BlazorApp run and checked over HTTP (pages, 401, 404, API key); BlazorApi.Demo.ConsoleClient run against it | passed |
| 2026-11-17 | After removing the dotnet-query integration: full suite (net8.0 + net10.0) + 5 demo tests | all passed |
| 2026-11-17 | M28 after review fixes, Release build: OpenApi 199, CodeGeneration 13, Integration 78 (intermediate hierarchies, cache incl. retries, API keys, large chunked bodies), SourceGenerator 14, Cli 24, Blazor 11, per framework (net8.0, net10.0); demo 5 | all passed |
| 2026-11-17 | M27, Release build: OpenApi 199, CodeGeneration 13, Integration 74 (alternate bodies, headers, progress, reconnection, logging), SourceGenerator 14, Cli 24 (mock server), Blazor 11, per framework (net8.0, net10.0); demo 5 | all passed |
| 2026-11-17 | M26, Release build: OpenApi 197 (JSONPath RFC 9535 examples, overlays, 3.2), CodeGeneration 13, Integration 68, SourceGenerator 14, Cli 23 (overlays, watch), Blazor 11 (stream view), per framework (net8.0, net10.0); demo 5 | all passed |
| 2026-11-17 | M25 gap closure: full suite (net8.0 + net10.0): OpenApi 142, CodeGeneration 11, Integration 50, SourceGenerator 12, Cli 21, Blazor 8, demo 5; `dotnet pack` of the source generator checked for the `BlazorApi.Runtime` dependency | all passed |
| 2026-11-17 | BlazorApi.Demo.BlazorApp rework: every contract operation served; browser end-to-end run in Chrome (list, filter, pagination, create with 422, merge patch, PUT, photo upload/download, order with 409, delete, inventory, 8 playground scenarios, not-found, mobile width, light and dark); full suite + 5 demo tests; console client against the app | all passed |

## Architectural decisions

1. **Own JSON and YAML parsers, no third-party dependencies in the engine.** The engine (`BlazorApi.OpenApi`, later
   `BlazorApi.CodeGeneration`) targets netstandard2.0 so the Roslyn source generator can compile it in. Shipping
   third-party parsers (or System.Text.Json) inside an analyzer causes assembly-version conflicts in IDE hosts. Owning
   the parsers also gives exact source positions for every diagnostic, insertion-ordered keys (deterministic output),
   lossless numbers, and hard limits for untrusted input (document size, nesting depth, YAML alias expansion).
2. **Layering.** `Syntax` (JSON/YAML to `DocNode`) -> `Document` (typed OpenAPI model, `OpenApiReader`) ->
   `References` (`OpenApiWorkspace`) -> `Validation` -> API model (`ApiModelBuilder`, pagination detection) ->
   generation plan -> emitters (models, clients, pagination, mocks). Runtime packages never reference the
   engine.
3. **Version normalization while reading.** OpenAPI 3.0 `nullable: true` becomes the `Null` type flag, 3.0 boolean
   `exclusiveMinimum`/`exclusiveMaximum` become the 3.1 numeric form, so later stages handle one model. 3.2 documents
   load as 3.1 with a warning; Swagger 2.0 is rejected with conversion guidance.
4. **References keep their identity.** A node is read once per element type, so a component schema reached through
   any number of references (or YAML aliases) is one model instance. `ReferenceTarget` holds the direct target;
   `Resolved` follows the chain. Schema references keep their 3.1 siblings for the API model to interpret.
5. **External references are restricted by default.** Local files under the root document's directory only; remote
   URLs and other schemes need an explicit policy; file access goes through `IDocumentResolver` (file system for the
   CLI, AdditionalFiles for the source generator, in-memory for tests).
6. **Diagnostics.** Stable identifiers (`BAPI1xxx` syntax, `2xxx` references, `3xxx` validation, `4xxx` model,
   `5xxx` configuration, `6xxx` generation, `9001` generator failure), MSBuild canonical format, JSON pointers, and
   per-identifier severity overrides (including suppression). Compatibility changes use their own stable kinds.
7. **Generated code shape.** One interface and class per client; each operation has `XxxAsync` (returns the
   value, throws `ApiException` subclasses) and `XxxResultAsync` (returns `ApiResult<T>`, never throws except for
   caller cancellation). Responses are described by a static `ApiResponseHandler<T>` per operation, so reading is
   explicit and reflection-free apart from System.Text.Json metadata.
8. **Nullability rules.** Required and nullable are independent. Required properties use the C# `required` modifier
   (enforced by System.Text.Json); optional properties are nullable and omitted when null; required nullable
   properties are always written. Optional nullable properties of JSON merge-patch bodies use `Optional<T>` so
   "absent" and "null" differ. Read-only and write-only properties are nullable (they are absent in one direction).
9. **Polymorphism uses generated converters, not `[JsonPolymorphic]`.** System.Text.Json on .NET 8 requires the
   discriminator to be the first JSON property and forbids declaring it as a property. Generated converters peek the
   discriminator anywhere in the object, keep it as a normal property, and map unknown values to an
   `Unrecognized{Type}` that preserves extra properties (forward compatible). Hierarchy roots are abstract.
10. **Unions.** Discriminated `oneOf` unions whose object variants are exclusive become abstract base classes;
    other unions become wrapper classes with `Is/As/TryGet/From` members dispatching by discriminator or JSON value
    kind; ambiguous unions keep the raw JSON and read variants on request (BAPI4002 warns).
11. **Integers without a format map to `long`** (unbounded in the specification; `int` overflows on real APIs).
12. **Golden files are compiled.** `tests/Golden` holds generated output; `BlazorApi.CodeGeneration.Tests` compares
    it, `BlazorApi.Integration.Tests` compiles it and runs it against recorded HTTP exchanges.
13. **Source generator compiles the engine in.** `BlazorApi.SourceGenerator` links the OpenApi and CodeGeneration
    sources (minus file I/O) so the analyzer has no dependencies. Contracts are `OpenApiContract` items (AdditionalFiles
    with `BlazorApiKind` metadata); referenced documents are `OpenApiReference` items. Each contract has its own
    pipeline that depends only on its text, options and the reference files, so C# edits and other contracts never
    re-run it.
14. **No query/caching integration.** The dotnet-query integration (`GenerateQueries`, the `invalidations` setting,
    `x-invalidates`, the `BlazorApi.DotNetQuery` package) was removed: it depended on `Msynk.DotNetQuery`, which is
    not published. Generated clients, `ApiResult` and the `IAsyncEnumerable` page walkers work with any caching
    library.
15. **Compatibility is judged from the client's side.** Changes are classified with request/response direction:
    widening a request type is safe, widening a response type breaks clients; new response enum values are
    potentially breaking because generated enums are strict.
16. **Conventions follow the sibling `dotnet-query` repository**: `.slnx`, `Directory.Build.props` with warnings as
   errors and analyzers, xunit 2.9, libraries for net8.0/net10.0, Roslyn components for netstandard2.0.
17. **Retries are safe by default and opt-in.** `ApiRetryHandler` retries only idempotent methods or requests with
    an idempotency key, honors `Retry-After`, and is added explicitly (`AddApiRetries`). It covers the common case
    without dependencies; circuit breakers, hedging and rate limiting are left to Microsoft.Extensions.Http.Resilience,
    whose rejections the runtime maps to results by exception type name (no Polly dependency).
18. **Per-call options derive a client** (`WithOptions`) instead of adding a parameter to every method: signatures,
    mocks and positional calls stay unchanged, and the derived client shares the `HttpClient`.
19. **Telemetry records templates, not values.** Spans and metrics carry `url.template` and the operation id, never
    URLs, parameters, headers or bodies, so instrumentation cannot leak credentials or personal data.
20. **Streams are values that own the response.** A streamed operation succeeds when the headers arrive; read
    failures surface during enumeration as the same `ApiException` family. Sequential media types (OpenAPI 3.2
    `itemSchema`) are read for every document version.
21. **Forward-compatible enums are opt-in** (`unknownEnumValues=unrecognized`), keeping decision 15's strict default;
    the extra member has no wire value.
22. **The JSON context is generated from the generated code.** The emitter collects the generic arguments of the
    runtime JSON helpers in the emitted sources plus every parameter, part and property type, so a type serialized
    anywhere cannot be missing; listing a superset is harmless. It is a CLI feature: source generators cannot feed the
    System.Text.Json generator (BAPI6005).
23. **Remote contracts are same-origin.** The CLI downloads URL contracts and their references only from the
    contract's origin, never local files, bounded by the parser's size limit and a timeout.
24. **Overlays apply to the parsed tree before reading.** The root document is deep-copied (YAML aliases become
    separate copies) and changed in place, so every later stage (references, validation, generation, diagnostics
    with overlay source positions) works unchanged. JSONPath is implemented in the engine (RFC 9535, I-Regexp mapped
    to .NET regular expressions with a time limit) to keep it dependency-free. Overlay errors stop loading.
25. **3.2 query strings reuse object query parameters.** A form-encoded `querystring` is exactly an exploded `form`
    object parameter, so the model converts it instead of adding a parameter kind to every later stage.
26. **Other success bodies are untyped content, not a union.** `ApiResult<T>` keeps one value type, so existing
    signatures and mocks stay; other bodies are read into `Content` and recovered with `TryGetContent<T>`.
27. **The mock server is a socket server.** `api-gen mock` speaks HTTP/1.1 over `TcpListener` (one request per
    connection, IPv4 and IPv6 loopback), so it needs no ASP.NET Core dependency, URL reservation or elevation.
28. **Intermediate classes read through a payload twin.** STJ cannot read a type while bypassing its own
    `[JsonConverter]`, so a subtree converter reads the class's exact JSON through a generated payload class with the
    same properties and attributes and copies the values. The root converter is unchanged and calls the intermediate
    converter, which never calls itself.
29. **The cache revalidates, it does not trust freshness.** Serving cached responses without asking the server would
    need full `Cache-Control` semantics; always revalidating keeps responses correct while saving the body transfer.
    Every caller-identifying request header is part of the key (fail closed: only known per-request headers are left
    out) because a typed client's handler pipeline is shared by every user of a server app. A review of this batch
    found and fixed: unbounded buffering, keys computed after inner handlers ran, conditions re-sent by outer
    retries, unbounded `Vary` bookkeeping, a sign-out race (a generation counter guards `Clear`), and the payload
    class making the root's discriminator required.

## Known limitations (current)

- Only the first success response body is the typed value; other JSON/text success bodies are untyped `object`s in
  `ApiResult.Content` (binary and streamed alternatives are not read).
- Server-sent event `retry:` fields are applied with the next event (the parser ignores `retry` in blocks without
  data); `ApiEventSource` falls back to `DefaultRetry`.
- `api-gen mock` does not validate requests and serves one example per response (no stateful behavior).
- `format: time` and `format: duration` map to `string` by default (lossless); `timeType=timeOnly` and
  `durationType=timeSpan` are opt-in.
- Overlays change the root document only, not documents it references with `$ref`.
- Generated JSON uses reflection-based System.Text.Json metadata by default. The CLI generates a
  `JsonSerializerContext` with `generateJsonContext=true`; with the source generator, trimmed/native AOT apps must
  supply one (generators cannot see each other's output).
- The conditional-request cache is in memory only (no persistent or distributed cache) and ignores `max-age`
  freshness: every GET is revalidated with the server.
- Generated mocks are emitted into the project that generates them (with `GenerateMocks=true`), which must then
  reference `BlazorApi.Testing`; generate mocks in the test project.

- YAML: explicit (`?`) and non-scalar mapping keys, anchors on implicit keys, recursive aliases and multi-document
  streams are rejected with BAPI1011. Tags other than the core schema are ignored.
- References: JSON Schema `$id`-based resolution and anchor references (`$ref: '#name'`, BAPI2005) are not
  supported; the `$dynamicRef` keyword is ignored with BAPI4001.
  Remote references require a host-provided resolver; the built-in file-system resolver does not download.
- Callbacks, links and webhooks are read (names only) and reported as not used for generation (BAPI3017, info).

## Review notes

- Security review (M24): generated-source injection through doc comments was possible with the C# line
  terminators U+2028, U+2029 and U+0085; `CodeWriter.Doc` now normalizes them. `SecurityTests` compiles a hostile
  contract (quotes, newlines, comment terminators and line separators in titles, paths, operation ids, parameter
  names, patterns, schema names, enum values and descriptions) and checks that no injected type exists. Integer enum
  values are emitted from parsed numbers. The threat model and mitigations are documented in docs/security.md.
- Documentation review (M24) found and fixed: a non-2xx status matched a success `default` response; numeric
  `const` values were emitted from source text; the source generator ignored `references.root` and the `contracts`
  list of its configuration file; the CLI ignored top-level defaults next to `contracts`, dropped `--option` errors in
  `validate` and accepted unknown `--fail-on` values. Each fix has a regression test.
- Packaging: the source generator package carries only the analyzer and MSBuild files (`SuppressDependenciesWhenPacking`).

## Ecosystem survey and gap inventory (M25, 2026-11-17)

Compared with .NET (NSwag, Kiota, Refit/Refitter, openapi-generator, Microsoft.Extensions.Http.Resilience), TypeScript
(openapi-typescript/openapi-fetch, Hey API, Orval), commercial SDK generators (Stainless, Speakeasy, Fern, APIMatic,
liblab), Go (oapi-codegen, ogen), Rust (progenitor), Python (openapi-python-client) and current standards (OpenAPI 3.2,
Overlay 1.1, RFC 9457, RFC 9110 `Retry-After`, the Idempotency-Key draft, OpenTelemetry semantic conventions).

| Gap or shortcoming | Found in | Resolution |
|---|---|---|
| Resilience-pipeline exceptions (Polly timeout, open circuit, rate limiter) escaped `XxxResultAsync` | runtime bug | Mapped to `Timeout`/`Network` errors |
| `api-gen validate` ignored `blazorapi.json` (options, reference policy, severity overrides) | CLI | Reads the configuration and `--config` |
| Source generator package did not declare `BlazorApi.Runtime` | packaging | Explicit `.nuspec` with the dependency |
| `application/x-ndjson` read as one JSON document | engine bug | Sequential media types are streams |
| No streaming (SSE, JSON Lines, `json-seq`, OpenAPI 3.2 `itemSchema`) | ST, SE, FE, HA, OGN | `ApiStream<T>`, `ServerSentEvent<T>` |
| No per-request overrides (headers, timeout, base address) | ST, FE, SE, KI, OC | `WithOptions`, `ApiClientOptions.Clone` |
| Operation- and path-level `servers` ignored; server variables not exposed | spec, few generators | `ServerAddress`, `OperationBaseAddresses`, `{Client}.Servers` |
| No retries with `Retry-After`; no idempotency keys | ST, SE, FE, MER | `ApiRetryHandler`, `AddApiRetries` |
| No OpenTelemetry instrumentation per operation | OGN, KI | `ApiTelemetry` spans and histogram |
| Strict enums only (new server values break reading) | SE, ST, OG, OC | `unknownEnumValues=unrecognized` |
| AOT required a hand-written `JsonSerializerContext` | RF, OG generichost | `generateJsonContext` (CLI), runtime context |
| CLI could not read contracts from URLs | every CLI surveyed | Same-origin URL loading |
| No overlays to fix third-party contracts | SE, Redocly, FE | Overlay 1.0-1.2 with RFC 9535 JSONPath (M26) |
| OpenAPI 3.2 `additionalOperations` and `querystring` ignored or rejected | Microsoft.OpenApi v3 | Supported (M26) |
| `format: duration` only as `string` | KI, OG | `durationType=timeSpan` (M26) |
| No watch mode | HA, OR | `api-gen generate --watch` (M26) |
| Only the first success body readable | OC, OGN, OF | `ApiResult.Content` (M27) |
| No typed response headers | ST (`withResponse`), RF, OC | `{Operation}ResponseHeaders` (M27) |
| No upload/download progress | rare elsewhere | `ApiTransferProgress` (M27) |
| No SSE reconnection | FE | `ApiEventSource.Reconnecting` (M27) |
| No operation logging or deprecation surfacing | MER logging, KI | `ApiLoggingHandler`, `blazorapi.deprecated` (M27) |
| No mock server | Prism, Microcks, OR (MSW), SE | `api-gen mock` (M27) |
| Intermediate hierarchy classes read as themselves | known limitation since M07 | Subtree converters (M28) |
| No conditional-request caching | essentially none surveyed | `ApiCacheHandler` (M28) |
| No UI for streams or infinite lists | HA/OR query integrations (front-end analogue) | Blazor `ApiStreamView` (M26) |

Considered and not done (see Known limitations and Deferred features): several typed success responses per
operation as a generated union,
custom templates and hooks, a mock server, OAuth authorization-code/device flows (delegated to identity libraries such
as Microsoft.Identity.Web, MSAL or Blazor's `IAccessTokenProvider`), MCP server generation.

## Deferred features

- Custom code templates (M22): the emitters are internal and not template-driven.
- Sharing schemas between contracts: each contract generates its own types in its own namespace.
- ASP.NET Core contract verification: the recommended approach is to export the server's OpenAPI document in CI and
  run `api-gen diff` against the published contract.
- A regression suite of large real-world contracts (GitHub, Stripe, Kubernetes): the repository has representative
  contracts (`tests/Contracts`) and a synthetic 500-schema contract in the benchmarks only.
- Generating a `JsonSerializerContext` for trimmed/AOT applications (possible from the CLI output; not from the
  source generator).
- Publishing: no packages are pushed; versions are `0.1.0-preview`.
- Several typed success bodies per operation as a generated result union (they are untyped in `ApiResult.Content`).
