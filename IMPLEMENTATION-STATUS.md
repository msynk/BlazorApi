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
| M14 | Query definitions and keys | Done |
| M15 | Query runtime integration (dotnet-query adapter `BlazorApi.DotNetQuery`) | Done |
| M16 | Mutation generation and invalidation (path relationships, `x-invalidates`, options) | Done |
| M17 | Pagination (cursor, page, offset, Link header, `x-pagination`), page walkers, infinite queries | Done |
| M18 | Mocking (`BlazorApi.Testing`: generated mocks, `MockApiHandler`) and fixtures | Done |
| M19 | Incremental source generator (`BlazorApi.SourceGenerator`) | Done |
| M20 | CLI (`api-gen generate/validate/diff/inspect`), JSON configuration, MSBuild items | Done |
| M21 | Contract compatibility analysis (`ContractComparer`) with policies and reports | Done |
| M22 | Advanced generation features | Partly: operation/tag filters, unused-model pruning, multi-contract projects, `x-pagination`, `x-invalidates`, `x-enum-varnames`; custom templates deferred |
| M23 | Blazor integration (`BlazorApi.Blazor`: `ApiResultView`, `ApiErrorView`, `ApiValidationErrors`; auth registration helpers `AddBearerToken`/`AddApiKey`/`AddBasicAuthentication` in `BlazorApi.Runtime`) | Done |
| M24 | Documentation, samples, benchmarks, CI workflow, packaging | Done (not published) |

## Tests executed

| Date | Scope | Result |
|---|---|---|
| 2026-11-09 | `dotnet test tests/BlazorApi.OpenApi.Tests` (net8.0, net10.0) | 104 passed |
| 2026-11-09 | `dotnet test BlazorApi.slnx` (OpenApi, CodeGeneration, Integration; net8.0 and net10.0) | 310 passed |
| 2026-11-09 | OpenApi (140), CodeGeneration (7), Integration (32), SourceGenerator (10), Cli (14), Blazor (8) per target framework (net8.0, net10.0) | 422 passed |
| 2026-11-09 | Full suite after security fixes: 430 tests (net8.0 + net10.0) + 5 sample tests | all passed |
| 2026-11-09 | `dotnet pack` of all 8 packages; a scratch net8.0 project consumed the SourceGenerator and Runtime packages from a local feed with warnings as errors | passed |
| 2026-11-09 | BenchmarkDotNet ShortRun (see docs/performance.md) | recorded |
| 2026-11-09 | After documentation-review fixes: 440 tests (net8.0 + net10.0) + 5 sample tests | all passed |
| 2026-11-09 | samples/Petstore.Tests (5); Petstore.BlazorApp run and checked over HTTP (pages, 401, 404, API key); Petstore.ConsoleClient run against it | passed |

## Architectural decisions

1. **Own JSON and YAML parsers, no third-party dependencies in the engine.** The engine (`BlazorApi.OpenApi`, later
   `BlazorApi.CodeGeneration`) targets netstandard2.0 so the Roslyn source generator can compile it in. Shipping
   third-party parsers (or System.Text.Json) inside an analyzer causes assembly-version conflicts in IDE hosts. Owning
   the parsers also gives exact source positions for every diagnostic, insertion-ordered keys (deterministic output),
   lossless numbers, and hard limits for untrusted input (document size, nesting depth, YAML alias expansion).
2. **Layering.** `Syntax` (JSON/YAML to `DocNode`) -> `Document` (typed OpenAPI model, `OpenApiReader`) ->
   `References` (`OpenApiWorkspace`) -> `Validation` -> API model (`ApiModelBuilder`, pagination detection) ->
   generation plan -> emitters (models, clients, pagination, queries, mocks). Runtime packages never reference the
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
14. **Query integration targets dotnet-query, it does not re-implement caching.** Generated `{Api}Queries` classes
    hold hierarchical keys (`All` > operation prefix > arguments), `QueryDefinition<T>` factories, mutation options
    with `Invalidates` derived from path relationships (ancestor/same/descendant paths) unless configured, and
    `InfiniteQuery` factories for paginated operations. `ApiQueryDefaults` retries only transient failures.
15. **Compatibility is judged from the client's side.** Changes are classified with request/response direction:
    widening a request type is safe, widening a response type breaks clients; new response enum values are
    potentially breaking because generated enums are strict.
16. **Conventions follow the sibling `dotnet-query` repository**: `.slnx`, `Directory.Build.props` with warnings as
   errors and analyzers, xunit 2.9, libraries for net8.0/net10.0, Roslyn components for netstandard2.0.

## Known limitations (current)

- **`Msynk.DotNetQuery` is not on nuget.org.** `BlazorApi.DotNetQuery` references version 0.8.0, which restores here
  only from the local NuGet cache. Publish dotnet-query (or add its feed) before building elsewhere or packing.
  The dotnet-query Blazor adapter is not available as a package, so the Blazor sample subscribes to query state
  itself instead of using its `<Query>` component.
- Polymorphic deserialization dispatches from the hierarchy root. A response typed as an intermediate concrete
  class (for example `Dog` with a `Puppy` subtype) is read as that class, not the subtype.
- Only the first success response body is typed per operation (BAPI6001 warns when others differ).
- `format: time` and `format: duration` map to `string` by default (lossless); `TimeType=TimeOnly` is opt-in.
- Generated JSON uses reflection-based System.Text.Json metadata by default. Trimmed/native AOT apps must supply a
  `JsonSerializerContext` for the generated types through `ApiClientOptions.JsonSerializerOptions`; the source
  generator cannot emit one because generators cannot see each other's output.
- Query keys include every operation argument, including header parameters.
- Invalidation is prefix-based (all argument combinations of a query), not per-argument.
- Infinite queries for Link-header pagination keep the next-page value in a weak table keyed by page instance.
- The source generator package does not yet declare its dependency on `Msynk.BlazorApi.Runtime`; consumers add it.
- Generated mocks are emitted into the project that generates them (with `GenerateMocks=true`), which must then
  reference `Msynk.BlazorApi.Testing`; generate mocks in the test project.
- `api-gen validate` does not read `blazorapi.json` (it takes `--option` and `--strict`).

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

## Deferred features

- Custom code templates (M22): the emitters are internal and not template-driven.
- Sharing schemas between contracts: each contract generates its own types in its own namespace.
- ASP.NET Core contract verification: the recommended approach is to export the server's OpenAPI document in CI and
  run `api-gen diff` against the published contract.
- A regression suite of large real-world contracts (GitHub, Stripe, Kubernetes): the repository has representative
  contracts (`tests/Contracts`) and a synthetic 500-schema contract in the benchmarks only.
- Generating a `JsonSerializerContext` for trimmed/AOT applications (possible from the CLI output; not from the
  source generator).
- Per-argument query invalidation and optimistic-update helpers beyond dotnet-query's own `OptimisticUpdates`.
- Publishing: no packages are pushed; versions are `0.1.0-preview`.
