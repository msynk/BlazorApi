# Source generator

`Msynk.BlazorApi.SourceGenerator` is a Roslyn incremental generator. It generates clients while you build and while
the IDE analyzes your code, from contracts declared as MSBuild items. It needs the .NET 8 SDK or later.

## Package references

```xml
<ItemGroup>
  <PackageReference Include="Msynk.BlazorApi.SourceGenerator" Version="0.1.0-preview" PrivateAssets="all" />
  <PackageReference Include="Msynk.BlazorApi.Runtime" Version="0.1.0-preview" />
</ItemGroup>
```

The generator package contains only the analyzer (with the OpenAPI engine compiled in, so it loads no other
assemblies) and the MSBuild files `build/Msynk.BlazorApi.SourceGenerator.props` and `.targets`. It does not declare
a dependency on `Msynk.BlazorApi.Runtime`; reference the runtime yourself, otherwise the generated code does not
compile (`CS0246` for `BlazorApi.Runtime`). Add `Msynk.BlazorApi.DotNetQuery` for `GenerateQueries=true` and
`Msynk.BlazorApi.Testing` for `GenerateMocks=true`.

## Items and metadata

```xml
<ItemGroup>
  <OpenApiContract Include="Contracts/petstore.yaml"
                   Namespace="MyApp.Petstore"
                   ClientName="PetstoreApi"
                   Options="GenerateQueries=true;Collections=mutable"
                   Config="Contracts/blazorapi.json" />
  <OpenApiReference Include="Contracts/shared/**/*.yaml" />
</ItemGroup>
```

| Item / metadata | Meaning |
|---|---|
| `OpenApiContract` | A contract to generate from. Each contract is generated independently. |
| `Namespace` | Namespace of the generated code. Default: `$(RootNamespace).<FileName>` (`MyApp.Petstore`); the file name is taken up to its first dot and converted to PascalCase. |
| `ClientName` | Client class name (default: from the API title). |
| `Options` | `key=value` pairs separated by `;` ([Configuration](configuration.md#keyvalue-syntax)). |
| `Config` | A JSON configuration file, relative to the project. It is added as an AdditionalFile automatically. |
| `OpenApiReference` | Documents that contracts reference with `$ref`. |

The targets turn these items into `AdditionalFiles` with a `BlazorApiKind` metadata (`Contract`, `Reference` or
`Config`); other AdditionalFiles are ignored. Precedence: defaults, then `Config`, then `Namespace`/`ClientName`,
then `Options`.

Things that only the JSON form can set: `invalidations`, `diagnostics` severities and `references` (`policy`, and
`root` relative to the configuration file). With a `contracts` array, the entry whose `contract` names this contract
applies on top of the top-level keys; `output` is not used.

## Referenced documents

The generator never reads the file system. A contract that references another file (`$ref: 'shared/error.yaml'`)
needs that file as an `OpenApiReference` item; otherwise the build reports BAPI1010 at the `$ref`. Referenced files
must be inside the contract's directory (or below it): references that leave it report BAPI2002. Remote (`http`)
references cannot be resolved by the generator.

## Diagnostics in the IDE and build

Contract problems are reported as compiler diagnostics with the `BAPI` id, the contract (or referenced file) path,
line and column, and the JSON pointer in brackets:

```
Contracts/orders.yaml(6,7): error BAPI3002: Path template variable '{id}' of operation 'getA' (GET /a/{id}) has no 'in: path' parameter [#/paths/~1a~1{id}/get]
```

They appear in the error list and in build output. When loading or validation reports an error, nothing is
generated for that contract. An unexpected exception inside the generator is reported as `BAPI9001` with the
exception type and message; please report it with the contract that triggers it.

Change severities with the `diagnostics` object of a `Config` file ([Diagnostics](diagnostics.md)):

```json
{ "diagnostics": { "BAPI1008": "none", "BAPI4002": "error" } }
```

## Strict mode

`Options="TreatWarningsAsErrors=true"` (or `Strict=true`) reports every BlazorApi warning as an error, so the build
fails on contract warnings. It applies after severity overrides.

## Incrementality

- Each contract has its own pipeline that depends only on its text, its metadata, the `OpenApiReference` and
  `Config` files, and `RootNamespace`.
- Editing C# code never re-runs generation; editing one contract re-generates only that contract.
- Changing a reference or configuration file re-runs the contracts (they share that input).
- Generation is deterministic: identical inputs produce identical sources.

The generator's tests (`tests/BlazorApi.SourceGenerator.Tests`) check these guarantees with Roslyn's step tracking.

## Inspecting the generated files

In Visual Studio and Rider, generated files appear under Dependencies > Analyzers >
`BlazorApi.SourceGenerator` > `BlazorApi.SourceGenerator.BlazorApiGenerator`. Their hint names are the contract file
name (non-alphanumeric characters replaced by `_`) followed by the file path: `petstore_yaml.PetstoreClient.g.cs`,
`petstore_yaml.Models.Pet.g.cs`.

To write them to disk:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <!-- Optional: a custom location. Exclude it from compilation, it is already part of it. -->
  <CompilerGeneratedFilesOutputPath>Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
<ItemGroup>
  <Compile Remove="Generated/**" />
</ItemGroup>
```

Without `CompilerGeneratedFilesOutputPath` the files go to
`obj/<Configuration>/<TargetFramework>/generated/BlazorApi.SourceGenerator/BlazorApi.SourceGenerator.BlazorApiGenerator/`.

## Source generator or CLI

| | Source generator | CLI (`api-gen generate`) |
|---|---|---|
| Generated code | in the compilation, not in source control | files you commit and review |
| Contract changes | picked up on build | regenerate, `--check` in CI |
| Several contracts | one `OpenApiContract` item each | `contracts` array in `blazorapi.json` |
| Reference root | the contract's directory, or `references.root` of the `Config` file | configurable (`references.root`) |
| JSON source generation for AOT | not possible in the same project (generators cannot see each other's output) | possible, the files are ordinary sources |

Both use the same engine and options and produce the same code.
