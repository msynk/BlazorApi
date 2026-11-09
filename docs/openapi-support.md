# OpenAPI support

## Versions

| Document | Handling |
|---|---|
| OpenAPI 3.0.x | Supported. Read into the same model as 3.1: `nullable: true` becomes a `null` type, boolean `exclusiveMinimum`/`exclusiveMaximum` become the 3.1 numeric form. |
| OpenAPI 3.1.x | Supported, including JSON Schema 2020-12 type arrays, `const`, `$defs`, `contentEncoding`, `examples` and `$ref` with sibling keywords. |
| OpenAPI 3.2.x | Read as 3.1 with a BAPI1003 warning; 3.2-only features (`additionalOperations`, sequential media types) are reported with BAPI3017 and ignored. |
| Swagger 2.0 | Rejected with BAPI1003. Convert it first (for example with the `swagger2openapi` tool). |
| No `openapi` field | BAPI1004. |

## JSON and YAML

The format is chosen from the file extension (`.json`, `.yaml`, `.yml`), otherwise from the first significant
character (`{` or `[` means JSON).

- **JSON**: strict RFC 8259 (no comments, no trailing commas).
- **YAML 1.2** subset used by API descriptions: block and flow collections, all scalar styles (plain, quoted,
  literal and folded with chomping and indentation indicators), comments, anchors and aliases, merge keys (`<<`) and
  core-schema tags. Other tags are ignored.
- **Not supported** (BAPI1011): explicit (`?`) keys, non-scalar keys, anchors on implicit keys, recursive aliases
  and multi-document streams.
- Duplicate keys report BAPI1005 (the last value wins). Unknown fields that are not `x-` extensions report BAPI1008.

Parsing is bounded by `ParserLimits` (64 MiB documents, nesting depth 256, 10,000,000 nodes after YAML alias
expansion); exceeding a limit reports BAPI1002. See [Security](security.md).

## References

- Local references (`#/components/schemas/Pet`) and references into other files (`schemas/pet.yaml#/Pet`,
  `common.yaml`) are resolved; a component reached through several references is one generated type.
- Reference cycles that never reach a definition report BAPI2003; recursive schemas (`Owner.manager: Owner`) are
  fine.
- Anchor references (`$ref: '#name'`, `$anchor`) report BAPI2005. `$id`-based resolution is not supported: a
  `$ref` is always resolved as a URI relative to its document. `$dynamicRef` is ignored with BAPI4001.

External references follow `ExternalReferencePolicy`:

| Policy | Allows |
|---|---|
| `Disabled` | Only references within the same document. |
| `LocalFiles` (default) | Files under the reference root: by default the directory of the root document. |
| `LocalFilesAndRemote` | Also `http`/`https` URLs, if the document resolver can fetch them. |

Other URI schemes are always rejected. A reference outside the policy or the reference root reports BAPI2002; an
unreadable file reports BAPI1010. The CLI reads referenced files from disk and never downloads; the source generator
only sees files added as `OpenApiReference` items. Remote documents must be downloaded and referenced locally, or
loaded through a custom `IDocumentResolver` passed in `OpenApiLoadOptions.DocumentResolver`.

## Type mapping

| OpenAPI | C# |
|---|---|
| `integer` (no format) | `long` (`integerWithoutFormat=int32` for `int`) |
| `integer` `int32` / `int64` | `int` / `long` |
| `integer` `int8` / `int16` / `uint8` / `uint16` / `uint32` / `uint64` | `sbyte` / `short` / `byte` / `ushort` / `uint` / `ulong` |
| `number` (no format) | `double` (`numberWithoutFormat=decimal` for `decimal`) |
| `number` `float` / `double` / `decimal` | `float` / `double` / `decimal` |
| `number` `int32` / `int64` | `int` / `long` |
| `boolean` | `bool` |
| `string` | `string` |
| `string` `date` | `DateOnly` (`dateType`: `DateTimeOffset` or `string`) |
| `string` `date-time` | `DateTimeOffset` (`dateTimeType=dateTime` for `DateTime`) |
| `string` `time` | `string` (`timeType=timeOnly` for `TimeOnly`) |
| `string` `duration` | `string` |
| `string` `uuid` / `guid` | `Guid` |
| `string` `uri` / `uri-reference` / `url` | `Uri` |
| `string` `byte`, or `contentEncoding: base64`/`base64url` | `byte[]` |
| `string` `binary`, or a binary `contentMediaType` | `FileContent` (request), `FileResponse` (response body) |
| `string` other known formats (`email`, `hostname`, `ipv4`, `password`, ...) | `string` |
| unknown format | the type's plain mapping, BAPI4007 (info) |
| `array` | `IReadOnlyList<T>` (see `collections`); `uniqueItems` does not change the type |
| `object` with only `additionalProperties` | `IReadOnlyDictionary<string, T>` |
| `object` with properties | a generated class |
| no type, `{}`, or several unrelated types | `JsonElement` (BAPI4005 for several types) |

## Required and nullable

Required (in the parent's `required` list) and nullable (`nullable: true` or a `null` type) are independent:

| Property | C# declaration | JSON |
|---|---|---|
| required, not nullable | `public required string Name { get; set; }` | always written; missing when reading is a deserialization error |
| required, nullable | `public required string? Summary { get; set; }` | always written, `null` included |
| optional | `public string? Tag { get; set; }` | omitted when null |
| optional nullable, merge-patch body | `public Optional<string?> Tag { get; set; }` | omitted when unset, `null` when set to null |
| `readOnly` or `writeOnly` | nullable, never `required` | omitted when null |
| `const` | initialized with the constant | always written |

- The `required` modifier is controlled by `useRequiredMembers`; System.Text.Json enforces it when reading.
- `Optional<T>` is used for models sent as `application/merge-patch+json` (`optionalNullable=auto`); it
  distinguishes "absent" (`Optional<T>.Absent`, the default) from "null". Assigning a value (including `null`)
  converts implicitly.
- OpenAPI 3.0 `allOf: [$ref]` with `nullable: true`, and 3.1 `anyOf: [$ref, {type: "null"}]`, become a nullable
  reference to the type.

## Composition

**allOf.** When exactly one `allOf` member is a referenced object schema (or one of several carries a
discriminator), it becomes the C# base class and the other members are flattened into the derived class.
Otherwise all members are flattened. The same property with different schemas in two members reports BAPI4003.

**Discriminated hierarchies.** A base schema with a `discriminator` whose subtypes extend it through `allOf` becomes
an abstract base class with a generated converter (`PetJsonConverter`). The converter finds the discriminator
anywhere in the object (System.Text.Json's `[JsonPolymorphic]` requires it first), keeps it as a normal property,
and reads unknown values as `UnrecognizedPet`, which keeps undeclared properties in `AdditionalProperties`. Each
subtype's constructor sets its discriminator value. Mappings that do not resolve report BAPI4009; a discriminator
property that is not defined reports BAPI3010.

Limitation: deserialization dispatches from the hierarchy root. A response typed as an intermediate concrete class
(`Dog` with a `Puppy` subtype) is read as that class.

**oneOf / anyOf.** A discriminated `oneOf`/`anyOf` whose variants are object schemas used by no other union and
without their own base type becomes an abstract base class (like a hierarchy, `Shape` with `Circle` and `Square`).
Other unions become sealed wrapper classes with `Variant`, `Value`, `Json`, `Is{Variant}`, `As{Variant}()`,
`TryGet{Variant}(...)` and `From{Variant}(...)` members, dispatching by discriminator or by JSON value kind (string,
number, boolean, array, object). Unions whose variants cannot be told apart report BAPI4002 and keep the raw JSON;
`TryGet{Variant}(JsonSerializerOptions, out value)` reads it as a variant on request. Properties declared next to
`oneOf`/`anyOf` are not generated (BAPI4001).

## Enums

String enums and integer enums become C# enums with a generated converter (`PetStatusJsonConverter`):

- Converters are strict: an unknown value fails with a `JsonException` naming the value, which a client reports as
  an `ApiDeserializationException`. New response enum values are therefore potentially breaking
  ([Compatibility](compatibility.md)).
- `PetStatusJsonConverter.ToWireValue(PetStatus.Available)` returns `"available"`;
  `PetStatusJsonConverter.TryParse("sold", out var status)` parses a wire value.
- Member names come from `x-enum-varnames` or `x-enumNames` when present; descriptions from `x-enum-descriptions` or
  `x-enumDescriptions`. Numeric values get names like `Value2` and `Minus1`.
- Integer enums outside the `int` range use `long` as the underlying type. Enums mixing strings and numbers, or
  without non-null values, are typed as `JsonElement` (BAPI4006).

## Operations, parameters and bodies

- Parameter styles: path `simple`/`label`/`matrix`, query `form`/`spaceDelimited`/`pipeDelimited`/`deepObject`,
  `header`, `cookie`, with `explode`, and `content`-described parameters (sent as JSON). Combinations without a
  standard serialization report BAPI4008. `Accept`, `Content-Type` and `Authorization` header parameters are ignored
  (BAPI3017), as the specification requires.
- Request bodies, in order of preference when several media types are declared (BAPI6002): JSON (`application/json`,
  `*+json`), `multipart/*`, `application/x-www-form-urlencoded`, `text/*`, binary. Inline multipart and form schemas
  become method parameters. XML bodies are exchanged as strings (BAPI4004); other media types (`image/png`,
  `application/octet-stream`, ...) as binary content (`FileContent` in requests, `FileResponse` in responses).
- Only the first success response body is typed; other success responses with a different body report BAPI6001.

## Not used for generation

Callbacks, links and webhooks are read (names only) and reported with BAPI3017 (info). Schema keywords without a
typed representation report BAPI4001:

| Keyword | Severity | Effect |
|---|---|---|
| `not`, `contains`, `propertyNames`, `unevaluatedItems` | info | only restrict values; the type accepts a superset |
| `patternProperties` | warning | matching properties are not typed |
| `if`, `then`, `else`, `dependentSchemas` | warning | conditional properties are not generated |
| `prefixItems`, array-form `items` | warning | tuple items are untyped JSON |
| `$dynamicRef`, non-boolean `unevaluatedProperties` | warning | the schema is generated without it |
