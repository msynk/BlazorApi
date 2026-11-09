# Diagnostics

Every problem BlazorApi finds is a diagnostic with a stable id, a severity, a message, a source location (file, line,
column) and, when known, a JSON pointer. The CLI prints them in the MSBuild format; the source generator reports them
as compiler diagnostics:

```
petstore.yaml(118,11): warning BAPI3009: Required property 'nmae' is not defined in 'properties' [#/components/schemas/Pet]
```

| Range | Area |
|---|---|
| BAPI1xxx | Syntax and document structure |
| BAPI2xxx | References |
| BAPI3xxx | Semantic validation |
| BAPI4xxx | API model construction |
| BAPI5xxx | Configuration |
| BAPI6xxx | Code generation |
| BAPI9001 | Internal source generator failure |

Errors stop generation for the contract (the CLI writes nothing for it; the build fails). Warnings mean something was
handled in a degraded way. Info never fails anything.

## Catalog

| Id | Default | Meaning |
|---|---|---|
| BAPI1001 | Error | The document is not well-formed JSON or YAML. |
| BAPI1002 | Error | A safety limit was exceeded (document length, nesting depth, YAML alias expansion, number of external documents). |
| BAPI1003 | Error | Unsupported OpenAPI version (including Swagger 2.0). A warning for 3.2 documents, which are read as 3.1. |
| BAPI1004 | Error | The document has no `openapi` field. |
| BAPI1005 | Warning | Duplicate key; the last value is used. |
| BAPI1006 | Error | A value has the wrong JSON type for its field. |
| BAPI1007 | Error | A field required by the specification is missing. |
| BAPI1008 | Warning | Unknown field (extensions must start with `x-`); it is ignored. |
| BAPI1009 | Error | Invalid field value (including an invalid `x-pagination`). |
| BAPI1010 | Error | A document cannot be read (missing contract or referenced file, no resolver). |
| BAPI1011 | Error | Unsupported YAML feature (complex keys, recursive aliases, multi-document streams). |
| BAPI2001 | Error | A `$ref` target does not exist. |
| BAPI2002 | Error | A `$ref` is not allowed by the external reference policy or leaves the reference root. |
| BAPI2003 | Error | References form a cycle that never reaches a definition. |
| BAPI2004 | Error | A `$ref` is malformed or empty. |
| BAPI2005 | Error | A recognized but unsupported reference form (anchor references). |
| BAPI3001 | Error | Two operations share an `operationId`. |
| BAPI3002 | Error | A path template variable has no `in: path` parameter. |
| BAPI3003 | Error | A path parameter does not appear in the path template. |
| BAPI3004 | Warning | A path parameter is not marked `required: true`; it is treated as required. |
| BAPI3005 | Error | A parameter (name and location) is declared twice. |
| BAPI3006 | Warning | An operation declares no responses. |
| BAPI3007 | Error | A security requirement names an undefined scheme. |
| BAPI3008 | Error | A path template is malformed. |
| BAPI3009 | Warning | A `required` entry names a property that is not defined. |
| BAPI3010 | Warning | A discriminator property is not defined by the schema or its variants. |
| BAPI3011 | Error | Two paths differ only in template variable names. |
| BAPI3012 | Error | A parameter style is not valid for its location or type. |
| BAPI3013 | Error | A parameter has both or neither of `schema` and `content`. |
| BAPI3014 | Warning | A `default`, `enum` or example value does not match the schema type. |
| BAPI3015 | Warning | A `pattern` is not a valid regular expression. |
| BAPI3016 | Error | A server URL uses an undefined variable. |
| BAPI3017 | Info | Not used for generation: callbacks, links, webhooks, ignored header parameters, 3.2-only fields. |
| BAPI3018 | Warning | Invalid constraint combination (for example `minLength` greater than `maxLength`). |
| BAPI4001 | Warning | A schema keyword has no typed representation (info for `not`, `contains`, `propertyNames`, `unevaluatedItems`). |
| BAPI4002 | Warning | Union variants cannot be told apart at runtime; the raw JSON is kept. |
| BAPI4003 | Warning | `allOf` members define the same property differently; the first definition is used. |
| BAPI4004 | Info | A media type has no typed serializer (for example XML); the body is exchanged as a string. |
| BAPI4005 | Warning | A schema allows several unrelated types; it is typed as `JsonElement`. |
| BAPI4006 | Warning | An enum value cannot be represented (duplicates, mixed types, no values). |
| BAPI4007 | Info | Unknown `format`; the type's plain mapping is used. |
| BAPI4008 | Warning | A parameter serialization without a standard form; a fallback is used. |
| BAPI4009 | Warning | A discriminator mapping does not resolve to a variant or subtype. |
| BAPI5001 | Error | Invalid option value (unknown enum value, invalid namespace or client name, malformed `key=value`). |
| BAPI5002 | Warning | Unknown option; it is ignored. |
| BAPI5003 | Error | Invalid configuration (not a JSON object, no `contract`, `Config` file not found). |
| BAPI6001 | Warning | An operation declares different success bodies; only the first is returned. |
| BAPI6002 | Info | An operation accepts several request media types; one is used. |
| BAPI6003 | Warning | An include/exclude filter matches no operation or tag. |
| BAPI6004 | Warning | No operations were selected for generation. |
| BAPI9001 | Error | The source generator failed unexpectedly (the message includes the exception). Please report it. |

Contract compatibility analysis does not produce diagnostics; it reports change kinds ([Compatibility](compatibility.md)).

## Changing severities

Use the `diagnostics` object of a JSON configuration (`blazorapi.json` for the CLI, `Config` metadata for the source
generator):

```json
{
  "diagnostics": {
    "BAPI1008": "none",
    "BAPI4007": "warning",
    "BAPI3009": "error"
  }
}
```

Values: `none` (also `hidden`, `suppress`) removes the diagnostic; `info` (also `suggestion`); `warning`; `error`.
In code, set `GeneratorOptions.SeverityOverrides` (and `OpenApiLoadOptions.SeverityOverrides` when loading documents
yourself); a `null` severity suppresses:

```csharp
var options = new GeneratorOptions
{
    SeverityOverrides = new Dictionary<string, DiagnosticSeverity?>
    {
        ["BAPI1008"] = null,
        ["BAPI3009"] = DiagnosticSeverity.Error,
    },
};
```

Overrides apply to diagnostics from loading, validation, the model and generation, not to configuration diagnostics
(BAPI5xxx) reported while the configuration itself is read. `api-gen validate` does not read a configuration file,
so overrides do not apply there.

## Strict mode

`TreatWarningsAsErrors=true` (`Strict=true`, or `--strict` on the CLI) reports every remaining warning as an error,
after overrides. Use it to keep contracts warning-free in CI.
