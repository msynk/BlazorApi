# CLI: api-gen

`api-gen` (package `Msynk.BlazorApi.Cli`, a .NET tool for .NET 8 and later) generates clients into files, validates
contracts, reports breaking changes and summarizes contracts.

```shell
dotnet tool install --global Msynk.BlazorApi.Cli --version 0.1.0-preview   # from your package feed
api-gen --help
```

From a clone of the repository: `dotnet run --project src/BlazorApi.Cli -f net10.0 -- <arguments>`.

## Commands

```
api-gen generate [<contract>] [--config <blazorapi.json>] [--output <dir>] [--namespace <ns>]
                 [--client-name <name>] [--option key=value]... [--dry-run] [--check] [--strict] [--quiet]
api-gen validate <contract>... [--strict] [--format text|json] [--option key=value]...
api-gen diff <previous> <current> [--format text|markdown|json] [--fail-on <level>] [--ignore <kind>[,<kind>...]]
             [--report <file>]
api-gen inspect <contract> [--format text|json]
api-gen --version | --help
```

Options take their value as the next argument or after `=` (`--format=json`).

**Exit codes**: `0` success; `1` failure (errors in a contract or configuration, out-of-date output with `--check`,
changes at the `--fail-on` level); `2` usage error (unknown command, missing argument, option without a value,
no arguments at all).

Diagnostics go to standard error in the MSBuild format (`file(line,col): error BAPI3002: message [pointer]`), so
CI systems and editors can link them.

## generate

```shell
api-gen generate Contracts/petstore.yaml --output src/Api/Generated --namespace MyApp.Petstore
api-gen generate --option GenerateQueries=true --option "Grouping=tag"
api-gen generate                       # reads ./blazorapi.json
api-gen generate --config build/api.json
```

| Option | Effect |
|---|---|
| `<contract>` | Contract path. Without it (and without `--config`), `blazorapi.json` in the current directory is used. |
| `--config` | A configuration file ([format](configuration.md#json-configuration-file)), possibly with several `contracts`. |
| `--output` | Output directory. Default: the configuration's `output`, else `Generated` (relative to the configuration file, or to the current directory without one). |
| `--namespace`, `--client-name` | Override the namespace and client name. Default namespace: the contract file name in PascalCase. |
| `--option key=value` | Any [option](configuration.md#options); repeatable; `;` separates several pairs in one value. |
| `--strict` | Warnings are errors. |
| `--dry-run` | List the changes (`create`, `update`, `delete`) without writing. |
| `--check` | Like `--dry-run`, and exit with `1` when anything would change. |
| `--quiet` | Hide info diagnostics and the summary line. |

When a contract has errors, nothing is written for it and the exit code is `1`; other contracts in the same
configuration are still processed. A positional contract replaces the configuration's `contract` only when the
configuration describes a single contract.

Files are written as UTF-8 without a byte order mark and with `\n` line endings. Existing files are compared with
line endings normalized, so a checkout that converts to CRLF does not appear out of date.

### Manifest and stale files

Each run writes `.blazorapi-manifest` in the output directory, listing the generated files. On the next run, files
listed in the previous manifest that are no longer generated (a removed schema or operation) are deleted. Files that
are not in the manifest, such as your own partial classes in the same directory, are never touched. Generated paths
are checked to stay inside the output directory. Commit the manifest with the generated code.

### In CI

```shell
api-gen generate --check     # with blazorapi.json; otherwise pass the same arguments as for generation
```

prints the pending changes and fails with `error: generated code is out of date; run 'api-gen generate' and commit
the result.` when the committed code does not match the contract.

## validate

Loads each contract, resolves references, validates it and plans generation, reporting every diagnostic. It exits
with `1` when any contract has an error.

```shell
$ api-gen validate orders.yaml
orders.yaml(6,7): error BAPI3002: Path template variable '{id}' of operation 'getA' (GET /a/{id}) has no 'in: path' parameter [#/paths/~1a~1{id}/get]
orders.yaml: 1 error(s), 0 warning(s), 0 operation(s), 0 type(s).
```

`--format json` prints `{"contract", "errors", "warnings", "diagnostics": [{"id", "severity", "message", "file",
"line", "column", "pointer"}]}` (an array for several contracts). `--strict` fails on warnings. `validate` does not
read `blazorapi.json`: references are restricted to the contract's directory and severity overrides do not apply.

## diff

Compares two versions of a contract from the point of view of existing clients ([Compatibility](compatibility.md)).

```shell
api-gen diff contracts/v1/petstore.yaml contracts/petstore.yaml
api-gen diff old.yaml new.yaml --format markdown --report api-changes.md --fail-on potentially-breaking
api-gen diff old.yaml new.yaml --ignore enum-value-added,response-added
```

| Option | Effect |
|---|---|
| `--format` | `text` (default), `markdown` (or `md`, a table for pull request comments), `json` |
| `--fail-on` | `breaking` (default), `potentially-breaking` (or `potential`), `non-breaking` (or `any`), `none`; other values are usage errors (exit code 2) |
| `--ignore` | Change kinds to drop from the report (repeatable, comma-separated) |
| `--report` | Also write the report to a file |

Example text output:

```
Breaking (1)
  POST /pets request body (application/json).status: A request property became required [property-now-required]
Potentially breaking (1)
  GET /pets response 200 body.items[].status: The enum value 'reserved' was added to a response (strict clients reject unknown values) [enum-value-added]
Non-breaking (1)
  GET /pets query parameter 'status': The enum value 'reserved' was added [enum-value-added]
```

Contracts are loaded without semantic validation (run `validate` for that); a contract that cannot be loaded exits
with `1`.

## inspect

Summarizes operations (method, path, operationId, return type, `paged:<style>`, `auth:<schemes>`, `deprecated`),
types and security schemes:

```
Petstore 1.0.0 (OpenAPI 3.0.3)
  server  https://api.example.com/v1

Operations (11)
  GET     /pets                                    listPets                 -> PetPage  [paged:cursor, auth:bearerAuth]
  GET     /store/inventory                         getInventory             -> map<integer(int32)>  [auth:apiKey]
  ...
```

`--format json` prints `{"title", "version", "operations": [{"method", "path", "operationId", "pagination"}],
"types": [{"name", "kind"}]}`.

## GitHub Actions

```yaml
name: api
on: pull_request

jobs:
  contract:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Install api-gen
        run: dotnet tool install --global Msynk.BlazorApi.Cli --version 0.1.0-preview
      - name: Validate the contract
        run: api-gen validate contracts/petstore.yaml --strict
      - name: Generated code is up to date   # reads blazorapi.json in the repository root
        run: api-gen generate --check
      - name: No breaking changes
        run: |
          git show "origin/${{ github.base_ref }}:contracts/petstore.yaml" > /tmp/previous.yaml
          api-gen diff /tmp/previous.yaml contracts/petstore.yaml --format markdown --report api-changes.md
      - name: Publish the report
        if: always()
        run: cat api-changes.md >> "$GITHUB_STEP_SUMMARY"
```

The tool must be available from a configured package source (packages are not on nuget.org yet). If the previous
contract references other files, check out the whole previous directory instead of a single file.
