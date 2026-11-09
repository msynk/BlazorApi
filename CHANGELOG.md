# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0-preview] - Unreleased

First preview. Packages: `Msynk.BlazorApi.OpenApi`, `.CodeGeneration`, `.Runtime`, `.SourceGenerator`, `.Cli`,
`.DotNetQuery`, `.Testing`, `.Blazor`. Libraries target .NET 8 and .NET 10; the engine and the source generator
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
- **dotnet-query integration** (`GenerateQueries`): hierarchical query keys, query definitions, mutations with
  invalidation from configuration, `x-invalidates` or path relationships, infinite queries, transient-only retry
  defaults (`ApiQueryDefaults`).
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
- Samples (Blazor Web App, console client, tests), documentation and a BenchmarkDotNet suite.

### Known limitations

- `Msynk.DotNetQuery` 0.8.0, required by `Msynk.BlazorApi.DotNetQuery`, is not on nuget.org; no BlazorApi package
  has been published yet.
- The source generator package does not declare its dependency on `Msynk.BlazorApi.Runtime`.
- Trimmed and native AOT applications must supply a `JsonSerializerContext` for the generated types.
- Polymorphic responses typed as an intermediate class are not dispatched to its subtypes; only the first success
  body of an operation is typed; `format: time` and `duration` map to `string` by default.
- See `IMPLEMENTATION-STATUS.md` for the complete list.
