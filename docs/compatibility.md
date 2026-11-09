# Contract compatibility

`ContractComparer` (namespace `BlazorApi.OpenApi.Compatibility`) compares two versions of a contract and classifies
every difference by its effect on clients built from the previous version. It powers `api-gen diff`
([CLI](cli.md#diff)) and can be used in tests ([Testing](testing.md#contract-compatibility-tests)).

## Severities

| Severity | Meaning |
|---|---|
| `Breaking` | Existing clients break: requests are rejected, or responses no longer match the client's types. |
| `PotentiallyBreaking` | Clients may break depending on how they and the server behave. |
| `NonBreaking` | Existing clients keep working. |

## Direction

Types are compared with the direction of data in mind, because generated clients write requests and read responses:

- **Requests**: widening is safe (`int32` to `int64`, a property becomes optional, a new enum value); narrowing
  breaks (a request property becomes required, an enum value is removed, the value can no longer be null).
- **Responses**: narrowing is safe (an enum value is removed); widening breaks or may break (a response value can
  now be null, `int32` to `int64`, a required property becomes optional or disappears). New response enum values
  are potentially breaking because generated enum converters are strict.
- New discriminator values are non-breaking: older clients read them as `Unrecognized*` types.

Operations are matched by method and path (path variable names are ignored); an operation found under a different
path with the same `operationId` is reported as moved.

## Change kinds

The kind is a stable identifier, usable with `--ignore` and `CompatibilityReport.Without`.

| Kind | Severity |
|---|---|
| `operation-removed` | Breaking |
| `operation-moved` | Breaking |
| `operation-added` | Non-breaking |
| `operation-id-changed` | Potentially breaking (generated method names change) |
| `operation-deprecated` | Non-breaking |
| `security-scheme-removed` | Potentially breaking |
| `security-scheme-changed` (type changed) | Breaking |
| `security-added` (an operation now requires authentication) | Breaking |
| `security-changed` (no previous requirement still accepted) | Breaking |
| `security-removed` | Non-breaking |
| `required-parameter-added` | Breaking |
| `optional-parameter-added` | Non-breaking |
| `parameter-removed` | Potentially breaking (clients still send it) |
| `parameter-now-required` | Breaking |
| `parameter-now-optional` | Non-breaking |
| `parameter-serialization-changed` (style, explode or content media type) | Breaking |
| `path-parameter-renamed` | Non-breaking (generated parameter names change) |
| `required-body-added` | Breaking |
| `optional-body-added` | Non-breaking |
| `body-removed` | Potentially breaking |
| `body-now-required` | Breaking |
| `request-media-type-removed` | Breaking |
| `response-removed` | Breaking for a success response, potentially breaking for an error response |
| `response-added` | Potentially breaking for a success response, non-breaking for an error response |
| `response-media-type-removed` | Breaking |
| `response-header-removed` (a required header) | Breaking |
| `property-added` | Non-breaking |
| `required-property-added` (request) | Breaking |
| `property-removed` | Response: breaking if it was required, otherwise potentially breaking. Request: potentially breaking |
| `property-now-required` (request) | Breaking |
| `property-now-optional` (response) | Breaking |
| `response-now-nullable` | Breaking |
| `request-no-longer-nullable` | Breaking |
| `nullability-changed` (the safe directions) | Non-breaking |
| `type-changed` | Breaking |
| `type-widened` (integer to a wider integer or to number) | Non-breaking in requests, breaking in responses |
| `type-narrowed` | Breaking in requests, non-breaking in responses |
| `format-changed` | Potentially breaking |
| `enum-value-added` | Potentially breaking in responses, non-breaking in requests |
| `enum-value-removed` | Breaking in requests, non-breaking in responses |
| `union-variant-added` (response) | Potentially breaking |
| `union-variant-removed` (request) | Breaking |
| `discriminator-value-added` | Non-breaking |
| `discriminator-value-removed` | Potentially breaking |

Read-only properties are not considered required in requests. Each change has a `Path` describing where it is, for
example `GET /pets response 200 body.items[].status` or `POST /pets query parameter 'limit'`.

## Reports

```csharp
CompatibilityReport report = ContractComparer.Compare(previousModel, currentModel);

report.Changes;                                         // most severe first, then by path
report.MaxSeverity;                                     // null when nothing changed
report.HasChangesAtLeast(ChangeSeverity.Breaking);
report.ToText(); report.ToMarkdown(); report.ToJson();
```

`ApiModel` instances come from `ApiModelBuilder.Build(OpenApiLoader.LoadFile(path).Document!, new DiagnosticBag())`.

- **Text**: changes grouped under `Breaking (n)`, `Potentially breaking (n)`, `Non-breaking (n)`; `No changes.` when
  there are none.
- **Markdown**: an `## API contract changes` heading and a table (severity, where, change, kind), suitable for pull
  request comments.
- **JSON**: `{"maxSeverity": "breaking", "changes": [{"severity", "kind", "path", "message"}]}`; severities are
  `breaking`, `potentially-breaking` and `non-breaking`.

## Policies

A policy decides which changes are acceptable:

- **Threshold**: `api-gen diff --fail-on breaking|potentially-breaking|non-breaking|none`, or
  `report.HasChangesAtLeast(...)` in code.
- **Accepted kinds**: `--ignore kind1,kind2`, or `report.Without(["enum-value-added"])` in code. For example, an API
  whose clients tolerate unknown enum values can ignore `enum-value-added`.

A common setup keeps the last released contract in the repository (`samples/Contracts/baseline/petstore.yaml` in the
samples) or reads it from the base branch in CI, and fails pull requests that introduce breaking changes.

For server projects, export the server's OpenAPI document in CI and run `api-gen diff` against the published contract.
