using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Document;

/// <summary>JSON Schema instance types.</summary>
[Flags]
public enum JsonSchemaTypes
{
    /// <summary>No explicit type.</summary>
    None = 0,

    /// <summary><c>null</c>.</summary>
    Null = 1,

    /// <summary><c>boolean</c>.</summary>
    Boolean = 2,

    /// <summary><c>integer</c>.</summary>
    Integer = 4,

    /// <summary><c>number</c>.</summary>
    Number = 8,

    /// <summary><c>string</c>.</summary>
    String = 16,

    /// <summary><c>array</c>.</summary>
    Array = 32,

    /// <summary><c>object</c>.</summary>
    Object = 64,
}

/// <summary>
/// The Schema Object, covering the OpenAPI 3.0 dialect and JSON Schema 2020-12 (OpenAPI 3.1). Version differences
/// are normalized while reading: 3.0 <c>nullable: true</c> adds <see cref="JsonSchemaTypes.Null"/> to
/// <see cref="Types"/>, and 3.0 boolean <c>exclusiveMinimum</c>/<c>exclusiveMaximum</c> become the 3.1 numeric form.
/// </summary>
public sealed class OpenApiSchema : OpenApiReferenceable<OpenApiSchema>
{
    /// <summary>
    /// For boolean schemas (3.1): <see langword="true"/> accepts anything, <see langword="false"/> nothing.
    /// <see langword="null"/> for object schemas.
    /// </summary>
    public bool? BooleanSchema { get; internal set; }

    /// <summary>The declared instance types (with <see cref="JsonSchemaTypes.Null"/> for nullable).</summary>
    public JsonSchemaTypes Types { get; internal set; }

    /// <summary>Whether <c>type</c> was declared.</summary>
    public bool HasType => (Types & ~JsonSchemaTypes.Null) != JsonSchemaTypes.None;

    /// <summary>Whether null is an allowed value (3.0 <c>nullable</c> or 3.1 <c>"null"</c> type).</summary>
    public bool IsNullable => (Types & JsonSchemaTypes.Null) != 0;

    /// <summary>Whether the 3.0 <c>nullable</c> keyword was present (with any value).</summary>
    public bool HasNullableKeyword { get; internal set; }

    /// <summary>The format.</summary>
    public string? Format { get; internal set; }

    /// <summary>The title.</summary>
    public string? Title { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }

    /// <summary>The default value.</summary>
    public DocNode? Default { get; internal set; }

    /// <summary>The <c>example</c> value.</summary>
    public DocNode? Example { get; internal set; }

    /// <summary>The <c>examples</c> array (3.1).</summary>
    public IReadOnlyList<DocNode> Examples { get; internal set; } = [];

    /// <summary>The allowed values, or empty.</summary>
    public IReadOnlyList<DocNode> Enum { get; internal set; } = [];

    /// <summary>Whether <c>enum</c> was declared.</summary>
    public bool HasEnum { get; internal set; }

    /// <summary>The <c>const</c> value (3.1).</summary>
    public DocNode? Const { get; internal set; }

    /// <summary>Object properties in document order.</summary>
    public OrderedMap<OpenApiSchema> Properties { get; internal set; } = OrderedMap<OpenApiSchema>.Empty;

    /// <summary>Required property names.</summary>
    public IReadOnlyList<string> Required { get; internal set; } = [];

    /// <summary>
    /// The <c>additionalProperties</c> schema, or <see langword="null"/> when absent or boolean (see
    /// <see cref="AdditionalPropertiesAllowed"/>).
    /// </summary>
    public OpenApiSchema? AdditionalProperties { get; internal set; }

    /// <summary>
    /// <see langword="false"/> for <c>additionalProperties: false</c>; otherwise <see langword="true"/>.
    /// </summary>
    public bool AdditionalPropertiesAllowed { get; internal set; } = true;

    /// <summary>Whether <c>additionalProperties</c> was declared (as a schema or boolean).</summary>
    public bool HasAdditionalProperties { get; internal set; }

    /// <summary>The array item schema.</summary>
    public OpenApiSchema? Items { get; internal set; }

    /// <summary>Composition: all of.</summary>
    public IReadOnlyList<OpenApiSchema> AllOf { get; internal set; } = [];

    /// <summary>Composition: exactly one of.</summary>
    public IReadOnlyList<OpenApiSchema> OneOf { get; internal set; } = [];

    /// <summary>Composition: any of.</summary>
    public IReadOnlyList<OpenApiSchema> AnyOf { get; internal set; } = [];

    /// <summary>The discriminator.</summary>
    public OpenApiDiscriminator? Discriminator { get; internal set; }

    /// <summary>Whether the property is only sent in responses.</summary>
    public bool ReadOnly { get; internal set; }

    /// <summary>Whether the property is only sent in requests.</summary>
    public bool WriteOnly { get; internal set; }

    /// <summary>Whether the schema is deprecated.</summary>
    public bool Deprecated { get; internal set; }

    /// <summary>The inclusive minimum.</summary>
    public decimal? Minimum { get; internal set; }

    /// <summary>The inclusive maximum.</summary>
    public decimal? Maximum { get; internal set; }

    /// <summary>The exclusive minimum.</summary>
    public decimal? ExclusiveMinimum { get; internal set; }

    /// <summary>The exclusive maximum.</summary>
    public decimal? ExclusiveMaximum { get; internal set; }

    /// <summary>The multiple-of constraint.</summary>
    public decimal? MultipleOf { get; internal set; }

    /// <summary>The minimum string length.</summary>
    public int? MinLength { get; internal set; }

    /// <summary>The maximum string length.</summary>
    public int? MaxLength { get; internal set; }

    /// <summary>The regular expression (ECMA-262 dialect).</summary>
    public string? Pattern { get; internal set; }

    /// <summary>The minimum item count.</summary>
    public int? MinItems { get; internal set; }

    /// <summary>The maximum item count.</summary>
    public int? MaxItems { get; internal set; }

    /// <summary>Whether items must be unique.</summary>
    public bool UniqueItems { get; internal set; }

    /// <summary>The minimum property count.</summary>
    public int? MinProperties { get; internal set; }

    /// <summary>The maximum property count.</summary>
    public int? MaxProperties { get; internal set; }

    /// <summary>The content encoding of a string (3.1), for example <c>base64</c>.</summary>
    public string? ContentEncoding { get; internal set; }

    /// <summary>The media type of a string's content (3.1).</summary>
    public string? ContentMediaType { get; internal set; }

    /// <summary>Schema definitions (<c>$defs</c>).</summary>
    public OrderedMap<OpenApiSchema> Definitions { get; internal set; } = OrderedMap<OpenApiSchema>.Empty;

    /// <summary>
    /// Keywords present on this schema that change validation but have no typed representation
    /// (<c>not</c>, <c>if</c>, <c>patternProperties</c>, <c>prefixItems</c>, <c>$dynamicRef</c>, ...).
    /// </summary>
    public IReadOnlyList<string> UnsupportedKeywords { get; internal set; } = [];

    /// <summary>
    /// Whether the schema declares anything besides annotations: used to decide whether a 3.1 <c>$ref</c> with
    /// siblings must be treated as a composition.
    /// </summary>
    public bool HasStructuralKeywords =>
        HasType || HasEnum || Const is not null || Properties.Count > 0 || Items is not null ||
        AllOf.Count > 0 || OneOf.Count > 0 || AnyOf.Count > 0 || HasAdditionalProperties || Discriminator is not null;
}

/// <summary>The Discriminator Object.</summary>
public sealed class OpenApiDiscriminator : OpenApiElement
{
    /// <summary>The name of the property that holds the discriminating value.</summary>
    public string PropertyName { get; internal set; } = string.Empty;

    /// <summary>Discriminator values mapped to schema names or references, in document order.</summary>
    public OrderedMap<string> Mapping { get; internal set; } = OrderedMap<string>.Empty;
}
