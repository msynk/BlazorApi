# BlazorApi

BlazorApi turns OpenAPI 3.0 and 3.1 contracts into strongly typed C# API clients for .NET and Blazor. It ships a
Roslyn source generator (generation at build time), a command-line tool (`api-gen`) for checked-in code and CI, a
small runtime for the generated code, and optional packages for testing and Blazor components.

The generation engine has no third-party dependencies: it has its own JSON and YAML parsers, reports problems as
stable `BAPIxxxx` diagnostics with file, line and JSON pointer, and produces deterministic output.

## Features

- OpenAPI 3.0 and 3.1 in JSON or YAML (3.2 `itemSchema` too), local `$ref` across files with a restrictive
  reference policy; the CLI also downloads contracts from URLs. OpenAPI 3.2 `QUERY`, `additionalOperations` and
  form-encoded `querystring` parameters.
- OpenAPI Overlays (1.0 to 1.2, RFC 9535 JSONPath) to fix or trim third-party contracts without editing them.
- Typed models: `required` members, nullability that follows the contract, `Optional<T>` for JSON merge-patch,
  DataAnnotations from schema constraints, strict or forward-compatible (`Unrecognized`) enum converters.
- Composition: `allOf` inheritance, discriminated hierarchies with forward-compatible `Unrecognized*` types,
  `oneOf`/`anyOf` unions.
- Clients with two methods per operation: `XxxAsync` (returns the value, throws `ApiException`) and
  `XxxResultAsync` (returns `ApiResult<T>`, never throws for API failures).
- Typed error bodies per status code, RFC 9457 problem details, distinct network/timeout/deserialization errors.
- Streamed responses: server-sent events and JSON Lines/NDJSON/`json-seq` as `IAsyncEnumerable` (`ApiStream<T>`),
  rendered incrementally by the Blazor `ApiStreamView`.
- Per-call options (`WithOptions`), document and operation-level servers with variables (`Servers`).
- Safe retries (`AddApiRetries`: idempotent requests or idempotency keys, `Retry-After`), compatible with
  Microsoft.Extensions.Http.Resilience; OpenTelemetry spans and metrics per operation.
- Trimming and native AOT: an optional generated `JsonSerializerContext` used by default.
- `IHttpClientFactory` registration, scheme-aware authentication handlers (bearer with refresh, API key, Basic,
  client credentials).
- Pagination detection (cursor, page, offset, `Link` header, `x-pagination`) with `IAsyncEnumerable` page and item
  walkers.
- Generated mocks and fixtures, an HTTP-level `MockApiHandler`, and contract compatibility analysis
  (`api-gen diff`).
- Blazor components for loading/error states, streamed and paged lists, and server-side validation errors in `EditForm`.

## Packages

| Package | Purpose |
|---|---|
| `BlazorApi.OpenApi` | Dependency-free OpenAPI engine: parsing, references, validation, API model, compatibility analysis. |
| `BlazorApi.CodeGeneration` | C# code generation from the API model (used by the CLI and the source generator). |
| `BlazorApi.Runtime` | Runtime for generated clients: HTTP execution, results and errors, JSON, authentication, DI. |
| `BlazorApi.SourceGenerator` | Roslyn source generator; generates clients at build time from `<OpenApiContract>` items. |
| `BlazorApi.Cli` | The `api-gen` .NET tool: `generate`, `validate`, `diff`, `inspect`. |
| `BlazorApi.Testing` | Base class of generated mocks, `MockApiHandler`, request recording and verification. |
| `BlazorApi.Blazor` | `ApiResultView`, `ApiStreamView`, `ApiErrorView`, `ApiValidationErrors` components. |

The packages are not published to nuget.org yet (see [Project status](#project-status)); build them locally with
`dotnet pack` as described in [Getting started](docs/getting-started.md#installing-the-packages).

## Quick start: source generator

```xml
<ItemGroup>
  <PackageReference Include="BlazorApi.SourceGenerator" Version="0.1.0-preview" PrivateAssets="all" />
  <PackageReference Include="BlazorApi.Runtime" Version="0.1.0-preview" />
</ItemGroup>

<ItemGroup>
  <OpenApiContract Include="Contracts/petstore.yaml" Namespace="MyApp.Petstore" />
</ItemGroup>
```

Build the project, then register and use the generated client:

```csharp
using MyApp.Petstore;

builder.Services.AddPetstoreClient(options => options.BaseAddress = new Uri("https://api.example.com/v1/"));

// Anywhere with dependency injection:
public sealed class PetService(IPetstoreClient api)
{
    public Task<Pet> GetAsync(long id, CancellationToken ct) => api.GetPetAsync(id, ct);
}
```

## Quick start: CLI

```shell
dotnet tool install --global BlazorApi.Cli --version 0.1.0-preview
api-gen generate Contracts/petstore.yaml --output Generated --namespace MyApp.Petstore

# In CI: fail when the checked-in code is out of date.
api-gen generate Contracts/petstore.yaml --output Generated --namespace MyApp.Petstore --check
```

Settings can also live in a `blazorapi.json` file, which `api-gen generate` reads when run without arguments.

The generated files are plain C#; the project only needs a reference to `BlazorApi.Runtime`.

## Using a generated client

```csharp
// Value method: returns the body or throws an ApiException subclass.
try
{
    Pet pet = await api.GetPetAsync(42);
}
catch (ApiException ex) when (ex.Content is Error error)   // typed error body declared for the status code
{
    Console.WriteLine($"{ex.StatusCode}: {error.Message}");
}

// Result method: never throws for API failures (only for caller cancellation).
ApiResult<Pet> result = await api.GetPetResultAsync(42);
if (result.IsSuccess)
{
    Console.WriteLine(result.Value!.Name);
}
else if (result.Error.Kind == ApiErrorKind.Http && result.Error.TryGetContent<Error>(out var error))
{
    Console.WriteLine(error.Message);
}
```

## Documentation

- [Getting started](docs/getting-started.md)
- [Configuration](docs/configuration.md)
- [Overlays](docs/overlays.md)
- [OpenAPI support and type mapping](docs/openapi-support.md)
- [Generated code](docs/generated-code.md)
- [Authentication](docs/authentication.md)
- [Pagination](docs/pagination.md)
- [Resilience and telemetry](docs/resilience-and-telemetry.md)
- [Testing](docs/testing.md)
- [Source generator](docs/source-generator.md)
- [CLI](docs/cli.md)
- [Contract compatibility](docs/compatibility.md)
- [Blazor](docs/blazor.md)
- [Diagnostics](docs/diagnostics.md)
- [Architecture](docs/architecture.md)
- [Performance](docs/performance.md)
- [Security](docs/security.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Changelog](CHANGELOG.md)

Demo projects are in [`demo/`](demo): a Blazor Web App (`BlazorApi.Demo.BlazorApp`), a console client
(`BlazorApi.Demo.ConsoleClient`) and a test project (`BlazorApi.Demo.Tests`).

## Project status

Preview, version `0.1.0-preview`. The feature set above is implemented and tested on .NET 8 and .NET 10. No
packages have been pushed to nuget.org yet. Known limitations are listed in [IMPLEMENTATION-STATUS.md](IMPLEMENTATION-STATUS.md)
and in the relevant documentation pages.

## License

MIT. See [LICENSE](LICENSE).
