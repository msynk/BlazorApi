# Overlays

An [OpenAPI Overlay](https://spec.openapis.org/overlay/latest.html) changes a description without editing it: fix a
third-party contract, add `operationId`s, remove internal operations, add extensions such as `x-pagination`. BlazorApi
applies overlays (Overlay 1.0, 1.1 and 1.2) after parsing the contract and before reading it, so every later step
(references, validation, generation) sees the changed document.

```yaml
# fixes.overlay.yaml
overlay: 1.1.0
info: { title: Petstore fixes, version: '1' }
actions:
  - target: $.paths['/health'].get
    update: { operationId: ping }
  - target: $.paths[*][?@.tags[0] == 'internal']   # every operation tagged 'internal'
    remove: true
  - target: $.components.schemas.Pet.properties.name
    update: { maxLength: 100 }
```

## Using overlays

| Host | How |
|---|---|
| Source generator | `<OpenApiContract Include="petstore.yaml" Overlays="fixes.overlay.yaml;more.overlay.yaml" />`; overlays listed in a `Config` file need an `<OpenApiOverlay Include="..." />` item each |
| CLI | `api-gen generate petstore.yaml --overlay fixes.overlay.yaml` (repeatable; also for `validate`) |
| Configuration file | `"overlays": ["fixes.overlay.yaml"]`, relative to the configuration file (also inside `contracts` entries) |
| Code | `new OpenApiLoadOptions { Overlays = [new OpenApiOverlay(path, text)] }` |

Overlays apply in order: the configuration's first, then `--overlay` or `Overlays` metadata. Each action sees the
result of the previous ones. Overlays change the root document only, not documents it references with `$ref`.

## Actions

| Field | Meaning |
|---|---|
| `target` (required) | An RFC 9535 JSONPath selecting the nodes to change. |
| `update` | Merged into each selected node: objects are merged recursively (new properties added, objects merged, arrays concatenated, primitive values replaced); an array target gets the value appended (or the items, for an array value); a primitive target is replaced. Other combinations are errors (BAPI7004). |
| `copy` (1.1) | A JSONPath selecting exactly one node, whose value is applied like `update`. |
| `remove: true` | Removes the selected nodes from their object or array (wins over `update` and `copy`). |
| `$ref` (1.2) | `#/components/actions/<name>`: the `fields` of a reusable action, overridden by the fields given next to the `$ref`. |

A target that selects nothing changes nothing and reports BAPI7003 (a warning, usually a typo). The selected nodes
of one action must all be objects, all arrays or all primitive values.

## JSONPath

Queries follow RFC 9535: `$`, `.name`, `['name']`, `.*`, `[*]`, `..` (descendants), indexes (`[0]`, `[-1]`), slices
(`[1:5:2]`), unions (`['a','b']`) and filters with comparisons (`==`, `!=`, `<`, `<=`, `>`, `>=`), `&&`, `||`, `!`,
parentheses, existence tests (`[?@.deprecated]`) and the functions `length`, `count`, `match`, `search` and `value`.
`match` and `search` use I-Regexp (RFC 9485) with a one-second time limit. `JsonPath.Parse` (namespace
`BlazorApi.OpenApi.Overlays`) is public for other uses.

## Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| BAPI7001 | Error | The overlay document or an action is invalid (missing `overlay`, `info` or `actions`, unsupported major version, an action without `target`, or with `update` and `copy` together). |
| BAPI7002 | Error | A `target` or `copy` is not a valid JSONPath. |
| BAPI7003 | Warning | A `target` selects no node. |
| BAPI7004 | Error | An update cannot be applied (incompatible types, removing the root, `copy` not selecting exactly one node). |
| BAPI7005 | Warning | The overlay version is newer than 1.2; it is applied with the 1.2 rules. |

When an overlay reports an error, the contract is not read and nothing is generated.
