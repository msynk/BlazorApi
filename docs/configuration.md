# Configuration

Code generation is controlled by `GeneratorOptions` (namespace `BlazorApi.CodeGeneration`). Every option has a
default suited to most APIs. Options can be set in three places:

- a JSON configuration file (`blazorapi.json`), read by the CLI and by the source generator (`Config` metadata);
- `key=value` pairs: MSBuild `Options` metadata of `OpenApiContract`, and the CLI `--option` flag;
- code, when calling `ApiClientGenerator.Generate` directly.

## Options

Keys are case-insensitive. Enum values are case-insensitive and ignore `-` and `_` (`readOnly`, `read-only` and
`READONLY` are equivalent).

| Option | Default | Accepted values and meaning |
|---|---|---|
| `namespace` | see below | Namespace of generated code. Must be a valid C# namespace (BAPI5001 otherwise). |
| `modelsNamespace` | same as `namespace` | Separate namespace for models (and fixtures). |
| `clientName` | from the API title | Client class name (`Petstore` becomes `PetstoreClient`; a trailing "API" word is dropped). With tag grouping, the name of the client for untagged operations. |
| `grouping` | `single` | `single`: one client. `tag`: one client per operation's first tag (`PetsClient`), untagged operations go to the default client. |
| `internalTypes` (alias `internal`) | `false` | `true` makes generated types `internal`. |
| `integerWithoutFormat` | `int64` | C# type of `type: integer` without a format: `int32` or `int64`. |
| `numberWithoutFormat` | `double` | C# type of `type: number` without a format: `double` or `decimal`. |
| `dateType` | `dateOnly` | `format: date`: `dateOnly`, `dateTimeOffset` or `string`. |
| `dateTimeType` | `dateTimeOffset` | `format: date-time`: `dateTimeOffset` or `dateTime`. |
| `timeType` | `string` | `format: time`: `string` (lossless) or `timeOnly` (fails on values with an offset). |
| `collections` | `readOnly` | `readOnly`: `IReadOnlyList<T>`/`IReadOnlyDictionary<string,T>`. `mutable`: `List<T>`/`Dictionary<string,T>`. `array`: `T[]`/`Dictionary<string,T>`. Parameters always accept `IEnumerable<T>`. |
| `optionalNullable` | `auto` | Optional properties that may be null. `auto`: `Optional<T>` for JSON merge-patch bodies, plain nullable elsewhere. `nullable`: always plain nullable. `optional`: always `Optional<T>`. |
| `useRequiredMembers` | `true` | Required properties use the C# `required` modifier (also enforced by System.Text.Json when reading). |
| `generateInterfaces` | `true` | Generate `I{Client}` interfaces. |
| `generateDependencyInjection` | `true` | Generate `Add{Client}` registration methods. |
| `generateQueries` | `false` | Generate dotnet-query keys, definitions and mutations ([Queries](queries.md)). |
| `generateMocks` | `false` | Generate client mocks and model fixtures ([Testing](testing.md)). |
| `generateDataAnnotations` | `true` | Add `System.ComponentModel.DataAnnotations` attributes for schema constraints. |
| `generateDocumentation` | `true` | Generate XML documentation comments. |
| `includeOperations` | all | Operation ids to generate (comma- or space-separated in `key=value` form). |
| `excludeOperations` | none | Operation ids to skip. |
| `includeTags` | all | Generate only operations with one of these tags. |
| `excludeTags` | none | Skip operations with any of these tags. |
| `includeUnusedModels` | `true` | Generate every component schema. Set `false` with operation filters to generate only the models the selected operations use. |
| `treatWarningsAsErrors` (alias `strict`) | `false` | Report warnings as errors, so generation and validation fail on them. |

Default namespace: the CLI uses the contract file name in PascalCase (`petstore.yaml` gives `Petstore`); the source
generator uses `$(RootNamespace).` followed by that name.

A filter value that matches no operation reports BAPI6003; filters that select nothing report BAPI6004. Unknown keys
report BAPI5002 and are ignored; invalid values report BAPI5001.

Two settings exist only in the JSON form: `invalidations` and `diagnostics` (severity overrides).

## key=value syntax

Pairs are separated by `;`. List values use `,` (MSBuild uses `;` as its own separator).

```xml
<OpenApiContract Include="Contracts/petstore.yaml"
                 Options="GenerateQueries=true;Collections=mutable;IncludeTags=pets,store" />
```

```shell
api-gen generate petstore.yaml --option GenerateQueries=true --option "dateType=string;strict=true"
```

`--option` can be repeated; later values win.

## JSON configuration file

```json
{
  "contract": "Contracts/petstore.yaml",
  "output": "Generated",
  "namespace": "MyApp.Petstore",
  "grouping": "tag",
  "generateQueries": true,
  "references": { "policy": "localFiles", "root": "Contracts" },
  "include": { "tags": ["pets", "store"] },
  "exclude": { "operations": ["deletePet"] },
  "invalidations": { "placeOrder": ["getInventory"] },
  "diagnostics": { "BAPI1008": "none", "BAPI4007": "warning" }
}
```

| Key | Meaning |
|---|---|
| `$schema` | Ignored (allowed for editor support). |
| `contract` | Contract path, relative to the configuration file. CLI only. |
| `output` | Output directory, relative to the configuration file. Default `Generated`. CLI only. |
| `references.policy` | External `$ref` policy: `disabled`, `localFiles` (default) or `localFilesAndRemote`. |
| `references.root` | Directory referenced files must be inside, relative to the configuration file. Default: the contract's directory. |
| `include.operations`, `include.tags` | Arrays; same as `includeOperations`, `includeTags`. |
| `exclude.operations`, `exclude.tags` | Arrays; same as `excludeOperations`, `excludeTags`. |
| `invalidations` | Object mapping a mutation operationId to the query operationIds it invalidates ([Queries](queries.md#invalidation)). |
| `diagnostics` | Object mapping diagnostic ids to `none` (also `hidden`, `suppress`), `info` (also `suggestion`), `warning` or `error` ([Diagnostics](diagnostics.md)). |
| any option name | Any option from the table above, as a string, number or boolean. |

Remote references need a document resolver that can download them; neither the CLI nor the source generator has
one, so `localFilesAndRemote` only helps programmatic callers with their own `IDocumentResolver`
(see [OpenAPI support](openapi-support.md#references)).

### Several contracts

A configuration may list several contracts in a `contracts` array. Each entry is a configuration whose paths are
relative to the configuration file; the top-level keys are defaults for every entry.

```json
{
  "contracts": [
    { "contract": "petstore.yaml", "output": "gen/pets", "namespace": "Pets", "generateQueries": true },
    { "contract": "billing.json", "output": "gen/billing", "namespace": "Billing", "diagnostics": { "BAPI4002": "none" } }
  ]
}
```

The source generator reads the configuration named by the contract's `Config` metadata. With a `contracts` array it
applies the entry whose `contract` names that contract on top of the top-level keys; `output` is not used (see
[Source generator](source-generator.md)).

## Precedence

- Source generator: defaults, then the `Config` file, then `Namespace` and `ClientName` metadata, then `Options`.
- CLI: the configuration file, then `--namespace` and `--client-name`, then each `--option`, then `--strict`.

## Configuring in code

```csharp
using BlazorApi.CodeGeneration;
using BlazorApi.OpenApi;
using BlazorApi.OpenApi.Diagnostics;

var options = new GeneratorOptions
{
    Namespace = "MyApp.Petstore",
    GenerateQueries = true,
    SeverityOverrides = new Dictionary<string, DiagnosticSeverity?> { ["BAPI1008"] = null },  // null suppresses
};

// LoadFile reads referenced files from disk; Generate(text, path, options) is for self-contained documents.
GenerationResult result = ApiClientGenerator.Generate(OpenApiLoader.LoadFile(path), options);
foreach (ApiDiagnostic diagnostic in result.Diagnostics) { Console.Error.WriteLine(diagnostic); }
foreach (GeneratedFile file in result.Files) { /* file.Path ("Models/Pet.g.cs"), file.Content */ }
```

When loading or validation reports an error, `result.Files` is empty. Check `result.HasErrors` before using the
files (the CLI writes nothing when it is true).

`GeneratorOptionsReader.ReadJson` and `GeneratorOptionsReader.ApplyPairs` (namespace
`BlazorApi.CodeGeneration.Configuration`) parse the JSON and `key=value` forms.
