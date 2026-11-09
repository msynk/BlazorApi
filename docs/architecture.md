# Architecture

## Packages and layers

```
                 build time                                   run time
  +-------------------------------------+        +-----------------------------------+
  | Msynk.BlazorApi.SourceGenerator     |        | your application                  |
  | Msynk.BlazorApi.Cli (api-gen)       |        |   generated client and models     |
  +------------------+------------------+        +-----------------+-----------------+
                     |                                             |
  +------------------v------------------+        +-----------------v-----------------+
  | Msynk.BlazorApi.CodeGeneration      |        | Msynk.BlazorApi.Runtime           |
  |   options, naming, plan, emitters   |        |   HTTP, results, errors, JSON,    |
  +------------------+------------------+        |   authentication, DI              |
                     |                           +--+-------------+-------------+----+
  +------------------v------------------+           |             |             |
  | Msynk.BlazorApi.OpenApi             |      .DotNetQuery    .Testing      .Blazor
  |   syntax, document, references,     |
  |   validation, API model,            |
  |   compatibility                     |
  +-------------------------------------+
```

- **Engine** (`BlazorApi.OpenApi`, `BlazorApi.CodeGeneration`): netstandard2.0, no package dependencies. It knows
  nothing about HTTP at run time.
- **Hosts**: the CLI references the engine; the source generator compiles the engine's sources into its own
  assembly (minus the file I/O code), so the analyzer loads nothing else.
- **Runtime** (`BlazorApi.Runtime`, net8.0/net10.0): everything generated code calls. It never references the engine.
- **Integrations**: `BlazorApi.DotNetQuery` (retry defaults; the cache stays in dotnet-query), `BlazorApi.Testing`
  (mock base class, `MockApiHandler`) and `BlazorApi.Blazor` (components). Each depends only on the runtime.

## Data flow

```
text --SyntaxParser--> DocNode --OpenApiReader--> OpenApiDocument --OpenApiWorkspace--> resolved references
     --OpenApiValidator--> diagnostics --ApiModelBuilder--> ApiModel --GenerationPlanner--> GenerationPlan
     --emitters--> GeneratedFile[]
```

1. **Syntax.** `SyntaxParser` detects the format and runs `JsonParser` or `YamlParser`, producing `DocNode` trees
   with source positions, insertion-ordered keys and lossless numbers, within `ParserLimits`.
2. **Document.** `OpenApiReader` builds the typed OpenAPI model (`OpenApiDocument`, `OpenApiSchema`, ...),
   normalizing 3.0 into 3.1 forms and reporting structural problems (BAPI1xxx).
3. **References.** `OpenApiWorkspace` resolves every `$ref` across documents, through an `IDocumentResolver`
   (file system for the CLI, AdditionalFiles for the source generator, in memory for tests), enforcing the
   reference policy and detecting cycles (BAPI2xxx).
4. **Validation.** `OpenApiValidator` checks semantics: operation ids, path parameters, security schemes, parameter
   styles, constraints (BAPI3xxx). `OpenApiLoader.Parse` and `OpenApiLoader.LoadFile` run steps 1 to 4.
5. **API model.** `ApiModelBuilder` produces a language-neutral `ApiModel`: operations, parameters, bodies,
   responses, security, named types (objects, enums, unions), inheritance, discriminators and pagination
   (BAPI4xxx). `ContractComparer` works on this model.
6. **Plan.** The planner applies `GeneratorOptions`: selects operations and types, groups clients, decides unions
   versus hierarchies, and allocates unique C# names.
7. **Emitters.** Model, client, pagination, query and mock emitters write C# through an escaping `CodeWriter`,
   producing `GeneratedFile`s sorted by path. `ApiClientGenerator.Generate` is the single entry point for both hosts.

Diagnostics from every stage go into one `DiagnosticBag`, which applies severity overrides and sorts them by
location for deterministic output.

## Runtime flow of a call

```
GetPetAsync -> GetPetResultAsync -> ApiRequestBuilder (path, query, headers, body, security metadata)
            -> ApiConnection.SendAsync -> HttpClient pipeline (auth handlers, ...) -> ApiResponseHandler<T>
            -> ApiResult<T> -> GetValueOrThrow()
```

`ApiConnection` turns every outcome except caller cancellation into an `ApiResult`, so the value method is a thin
`GetValueOrThrow()` wrapper over the result method.

## Key design decisions

1. **Own JSON and YAML parsers.** Shipping third-party parsers or System.Text.Json inside an analyzer causes
   assembly-version conflicts in IDE hosts. Owning them also gives exact positions for diagnostics, ordered keys,
   lossless numbers and hard limits for untrusted input.
2. **Version normalization while reading.** Later stages handle one model; 3.2 loads as 3.1 with a warning.
3. **References keep their identity.** A schema reached through any number of references or YAML aliases is one
   model instance, and one generated type.
4. **Restrictive external references.** Local files under the root document's directory only, unless configured.
5. **Stable diagnostics.** Ids by range, MSBuild format, JSON pointers, per-id severity overrides.
6. **Two methods per operation.** `XxxAsync` throws, `XxxResultAsync` returns `ApiResult<T>`. Responses are
   described by a static `ApiResponseHandler<T>` per operation: explicit and reflection-free apart from
   System.Text.Json metadata.
7. **Required and nullable are independent.** `required` members, nullable optional properties omitted when null,
   `Optional<T>` for merge-patch, nullable read-only and write-only properties.
8. **Generated polymorphic converters instead of `[JsonPolymorphic]`**, which on .NET 8 requires the discriminator
   first and forbids declaring it as a property. Unknown discriminators map to `Unrecognized*` types.
9. **Unions**: abstract bases for exclusive discriminated unions, wrappers otherwise, raw JSON for ambiguous ones.
10. **Unformatted integers are `long`**, because `int` overflows on real APIs.
11. **Golden files are compiled and executed.** `tests/Golden` holds generated output that the code generation tests
    compare and the integration tests compile and run against recorded HTTP exchanges.
12. **Per-contract incremental pipelines** in the source generator, depending only on the contract's inputs.
13. **Query integration targets dotnet-query** instead of re-implementing caching.
14. **Compatibility is judged from the client's side**, with request/response direction.

Deferred work and known limitations are tracked in `IMPLEMENTATION-STATUS.md`.
