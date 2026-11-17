# Troubleshooting

## Nothing is generated

- Check that the contract is an `OpenApiContract` item (not `None` or `Content`) and that the generator's MSBuild
  files are imported: they are with the `BlazorApi.SourceGenerator` package; with a project reference, import
  `BlazorApi.SourceGenerator.props` and `.targets` yourself, as the demo projects do.
- Look for `BAPI` errors in the build output. When loading or validation reports an error, nothing is generated for
  that contract. Run `api-gen validate <contract>` for a quick report.
- BAPI6004 means the include/exclude filters selected no operation; BAPI6003 names a filter value that matches
  nothing (operation ids and tags are case-sensitive).
- Look at the generated files (see [Source generator](source-generator.md#inspecting-the-generated-files)).

## CS0246: the type or namespace 'BlazorApi' could not be found

The generated code uses `BlazorApi.Runtime`. The generator package brings it in as a dependency; with project
references (as in the demos) reference the runtime project yourself. Generated mocks also need `BlazorApi.Testing`.

## CS0400 in a Razor component: 'Pet' could not be found in the global namespace

A `[Parameter]` of a Razor component is typed with a model from the source generator. Razor compiles component
declarations without seeing other generators' output, so write the type fully qualified and non-nullable
(`global::MyApp.Api.Pet`). See [Blazor](blazor.md#generated-types-in-component-parameters).

## Generated code does not update

- The source generator re-runs when the contract, its metadata, an `OpenApiReference` or a `Config` file changes.
  If a referenced file is not an `OpenApiReference` item, its changes are not seen (and the build reports BAPI1010).
- IDEs sometimes keep an old analyzer loaded after the generator package is updated; restart the IDE or run
  `dotnet build-server shutdown`.
- With the CLI, run `api-gen generate` again; `api-gen generate --check` tells whether the files are current.

## BAPI1010: a referenced file cannot be read

- Source generator: add the file as `<OpenApiReference Include="..." />` (globs work). The generator never reads the
  file system directly.
- CLI: check the path relative to the referencing document.
- Remote URLs referenced from a local contract cannot be read by either; download the document and reference the
  local copy, or give the CLI the contract's URL (references on the same origin are then downloaded).

## BAPI2002: reference not allowed

The `$ref` leaves the reference root (by default the contract's directory), uses a remote URL, or uses a scheme other
than `file`. Set `references.root` in `blazorapi.json` to a common parent directory (with the source generator, attach that file
with the contract's `Config` metadata), and add the referenced files as `OpenApiReference` items.

## BAPI1003, BAPI1011 and other loading errors

- Swagger 2.0 (`swagger: "2.0"`): convert the document to OpenAPI 3 first.
- YAML features that are not supported (BAPI1011): complex or explicit `?` keys, recursive aliases, several documents
  in one file. Convert the document to JSON or simplify the YAML.
- See [Diagnostics](diagnostics.md) for every id, and lower the severity of warnings you accept.

## BAPI5001, BAPI5002, BAPI5003: configuration problems

- BAPI5002: a misspelled option name (keys are the `GeneratorOptions` property names, case-insensitive).
- BAPI5001: an invalid value, for example `Grouping=sideways`, or a namespace that is not a valid C# namespace.
- BAPI5003 with the source generator: the `Config` file was not found; use a path relative to the project file.
- `diagnostics` cannot be set with `key=value` pairs; use a JSON configuration.

## BAPI9001

The source generator failed internally. The message names the exception. Please open an issue with the contract (or
a reduced version) that triggers it. As a workaround, generate with the CLI.

## Trimming and native AOT

Generated code uses System.Text.Json with reflection-based metadata by default, which trimmed and native AOT apps
disable. The simplest fix is to generate the client with the CLI and `generateJsonContext=true`: it emits a
`{Api}JsonContext` that lists every type the client reads or writes (models, enums, subtypes and `Unrecognized*`
types, union variants, collections and dictionaries of bodies and parameters), and the generated constructors and
`Add{Client}` methods use it by default. The runtime's own types (`ApiProblemDetails`) are covered by the runtime.

```shell
api-gen generate petstore.yaml --output Generated --option generateJsonContext=true
```

To combine the generated context with your own options, pass it to `ApiJson.CreateOptions`:

```csharp
builder.Services.AddPetstoreClient(options =>
    options.JsonSerializerOptions = ApiJson.CreateOptions(PetstoreJsonContext.Default, MyOtherContext.Default));
```

With the source generator, write the context yourself, covering the same types:

```csharp
[JsonSerializable(typeof(Pet))]
[JsonSerializable(typeof(Dog))]
[JsonSerializable(typeof(Cat))]
[JsonSerializable(typeof(UnrecognizedPet))]
[JsonSerializable(typeof(PetPage))]
[JsonSerializable(typeof(NewPet))]
[JsonSerializable(typeof(Error))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
// ... every other generated model
internal partial class PetstoreJsonContext : JsonSerializerContext;

builder.Services.AddPetstoreClient(options => options.JsonSerializerOptions = ApiJson.CreateOptions(PetstoreJsonContext.Default));
```

The System.Text.Json source generator cannot see types produced by another source generator. With the BlazorApi
source generator, generate the client in a class library and declare the context in a project that references it,
or generate the client with the CLI (its output is ordinary source code).

## Deserialization errors

`ApiDeserializationException` (or `ApiErrorKind.Deserialization`) means the success body did not match the
contract:

- a required property is missing (the C# `required` modifier is enforced when reading; set
  `UseRequiredMembers=false` only if the server is known to omit it);
- an enum value the contract does not declare (generated converters are strict by default; update the contract, or
  generate with `unknownEnumValues=unrecognized` so new values read as an `Unrecognized` member);
- a value of the wrong JSON type, or a `format: time` value with an offset when `TimeType=timeOnly`.

`ApiError.Message` names the operation and the problem; `ApiError.Exception` has the `JsonException`.

## Authentication is not applied

- The `scheme` passed to `AddBearerToken`, `AddApiKey` or `AddBasicAuthentication` must equal the name under
  `components.securitySchemes`, and the operation must require it (operations with `security: []` never get
  credentials). `api-gen inspect` shows each operation's schemes.
- A 401 is not retried when the provider cannot refresh (`StaticAccessTokenProvider`), when `InvalidateAsync`
  returns `false`, when the new token equals the old one, or when the request body cannot be replayed (multipart,
  streams).

## Requests go to the wrong address

The base address is `ApiClientOptions.BaseAddress`, else `HttpClient.BaseAddress`, else the first server URL of the
contract (`{Client}.DefaultBaseAddress`). The base path is kept: with `https://host/v1/`, `/pets` goes to
`https://host/v1/pets`. Operations that declare their own `servers` (on the operation or its path) always use that
server; override it per operation with `ApiClientOptions.OperationBaseAddresses["operationId"]`.

## A resilience handler makes calls throw

Exceptions of a resilience pipeline in the handler chain (Microsoft.Extensions.Http.Resilience or Polly: an open
circuit, a rate limiter, a timeout strategy) are results like any other failure: a Polly timeout is an
`ApiErrorKind.Timeout` error, other rejections are `ApiErrorKind.Network` errors with the Polly exception in
`ApiError.Exception`. `XxxResultAsync` therefore does not throw for them.

## Other behaviors that surprise

- Only the first success body is the typed `Value` (BAPI6001); other success bodies are in `ApiResult.Content`.
- `format: time` and `format: duration` are `string` unless `TimeType=timeOnly` (time only).
- Generated mocks answer 501 for operations without a setup.
- `api-gen diff` exits with `1` on breaking changes by default; use `--fail-on none` to only report.
