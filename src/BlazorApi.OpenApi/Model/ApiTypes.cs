using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Model;

/// <summary>
/// The semantic kind of a primitive value. The API model stays language-neutral: how a kind maps to a concrete
/// type (for example whether an unformatted integer is 32 or 64 bits) is decided by code generation.
/// </summary>
public enum ApiPrimitiveKind
{
    /// <summary>A string (including formats without a dedicated kind, such as <c>email</c>).</summary>
    String,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>An integer; see <see cref="ApiPrimitiveType.Format"/> (<c>int32</c>, <c>int64</c>, ...).</summary>
    Integer,

    /// <summary>A number; see <see cref="ApiPrimitiveType.Format"/> (<c>float</c>, <c>double</c>, <c>decimal</c>).</summary>
    Number,

    /// <summary>An RFC 3339 full-date (<c>format: date</c>).</summary>
    Date,

    /// <summary>An RFC 3339 date-time (<c>format: date-time</c>).</summary>
    DateTime,

    /// <summary>An RFC 3339 time (<c>format: time</c>), which may include an offset.</summary>
    Time,

    /// <summary>An ISO 8601 duration (<c>format: duration</c>).</summary>
    Duration,

    /// <summary>A UUID (<c>format: uuid</c>).</summary>
    Uuid,

    /// <summary>A URI or URI reference (<c>format: uri</c>, <c>uri-reference</c>).</summary>
    Uri,

    /// <summary>Base64-encoded bytes inside JSON (<c>format: byte</c> or <c>contentEncoding: base64</c>).</summary>
    Base64,

    /// <summary>Raw binary content (<c>format: binary</c>, file uploads and downloads).</summary>
    Binary,

    /// <summary>Any JSON value (no type, free-form, or a construct without a typed representation).</summary>
    Any,
}

/// <summary>A reference to a type in the API model, including whether <c>null</c> is allowed.</summary>
public abstract class ApiTypeRef
{
    private protected ApiTypeRef(bool isNullable) => IsNullable = isNullable;

    /// <summary>Whether <c>null</c> is a permitted value.</summary>
    public bool IsNullable { get; }

    /// <summary>Returns this reference with the given nullability.</summary>
    public abstract ApiTypeRef WithNullable(bool nullable);

    /// <summary>A compact description, for example <c>array&lt;Pet&gt;?</c>.</summary>
    public abstract override string ToString();
}

/// <summary>A primitive type.</summary>
public sealed class ApiPrimitiveType : ApiTypeRef
{
    /// <summary>Creates a primitive type reference.</summary>
    public ApiPrimitiveType(ApiPrimitiveKind kind, string? format = null, bool isNullable = false, DocNode? constValue = null)
        : base(isNullable)
    {
        Kind = kind;
        Format = format;
        ConstValue = constValue;
    }

    /// <summary>The semantic kind.</summary>
    public ApiPrimitiveKind Kind { get; }

    /// <summary>The OpenAPI format, when one was declared.</summary>
    public string? Format { get; }

    /// <summary>The single permitted value (<c>const</c>, or a one-value enum on a property), when declared.</summary>
    public DocNode? ConstValue { get; }

    /// <inheritdoc />
    public override ApiTypeRef WithNullable(bool nullable) =>
        nullable == IsNullable ? this : new ApiPrimitiveType(Kind, Format, nullable, ConstValue);

    /// <inheritdoc />
    public override string ToString() =>
        Kind.ToString().ToLowerInvariant() + (Format is null ? string.Empty : "(" + Format + ")") + (IsNullable ? "?" : string.Empty);
}

/// <summary>An array type.</summary>
public sealed class ApiArrayType : ApiTypeRef
{
    /// <summary>Creates an array type reference.</summary>
    public ApiArrayType(ApiTypeRef items, bool uniqueItems = false, bool isNullable = false)
        : base(isNullable)
    {
        Items = items;
        UniqueItems = uniqueItems;
    }

    /// <summary>The item type.</summary>
    public ApiTypeRef Items { get; }

    /// <summary>Whether items must be unique (a set).</summary>
    public bool UniqueItems { get; }

    /// <inheritdoc />
    public override ApiTypeRef WithNullable(bool nullable) =>
        nullable == IsNullable ? this : new ApiArrayType(Items, UniqueItems, nullable);

    /// <inheritdoc />
    public override string ToString() => $"array<{Items}>" + (IsNullable ? "?" : string.Empty);
}

/// <summary>A string-keyed map (an object schema with only <c>additionalProperties</c>).</summary>
public sealed class ApiMapType : ApiTypeRef
{
    /// <summary>Creates a map type reference.</summary>
    public ApiMapType(ApiTypeRef values, bool isNullable = false)
        : base(isNullable) => Values = values;

    /// <summary>The value type.</summary>
    public ApiTypeRef Values { get; }

    /// <inheritdoc />
    public override ApiTypeRef WithNullable(bool nullable) =>
        nullable == IsNullable ? this : new ApiMapType(Values, nullable);

    /// <inheritdoc />
    public override string ToString() => $"map<{Values}>" + (IsNullable ? "?" : string.Empty);
}

/// <summary>A reference to a named type (object, enum or union).</summary>
public sealed class ApiNamedTypeRef : ApiTypeRef
{
    /// <summary>Creates a named type reference.</summary>
    public ApiNamedTypeRef(ApiNamedType target, bool isNullable = false)
        : base(isNullable) => Target = target;

    /// <summary>The referenced type.</summary>
    public ApiNamedType Target { get; }

    /// <inheritdoc />
    public override ApiTypeRef WithNullable(bool nullable) =>
        nullable == IsNullable ? this : new ApiNamedTypeRef(Target, nullable);

    /// <inheritdoc />
    public override string ToString() => Target.Name + (IsNullable ? "?" : string.Empty);
}

/// <summary>Where a named type comes from.</summary>
public enum ApiTypeOrigin
{
    /// <summary>A schema under <c>components/schemas</c> (of the root or an external document).</summary>
    Component,

    /// <summary>An anonymous schema that needs a type of its own (an inline object, enum or union).</summary>
    Inline,
}

/// <summary>A named type: object, enum or union.</summary>
public abstract class ApiNamedType
{
    private protected ApiNamedType(IReadOnlyList<string> nameHint, ApiTypeOrigin origin, OpenApiSchema schema)
    {
        NameHint = nameHint;
        Origin = origin;
        Schema = schema;
    }

    /// <summary>
    /// The parts a name is built from: the component name for components; the owner's hint plus a context word
    /// (property name, <c>Response</c>, <c>Item</c>, ...) for inline types. Code generation applies naming rules.
    /// </summary>
    public IReadOnlyList<string> NameHint { get; }

    /// <summary>The name hint joined with dots, for messages and debugging.</summary>
    public string Name => string.Join(".", NameHint);

    /// <summary>Where the type comes from.</summary>
    public ApiTypeOrigin Origin { get; }

    /// <summary>The schema the type was built from.</summary>
    public OpenApiSchema Schema { get; }

    /// <summary>The JSON pointer of <see cref="Schema"/>.</summary>
    public string Pointer => Schema.Pointer;

    /// <summary>The source location of <see cref="Schema"/>.</summary>
    public SourceLocation Location => Schema.Location;

    /// <summary>The title.</summary>
    public string? Title { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }

    /// <summary>Whether the type is deprecated.</summary>
    public bool Deprecated { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Validation constraints that code generation can document or enforce.</summary>
public sealed class ApiConstraints
{
    /// <summary>No constraints.</summary>
    public static ApiConstraints None { get; } = new();

    /// <summary>The minimum string length.</summary>
    public int? MinLength { get; internal set; }

    /// <summary>The maximum string length.</summary>
    public int? MaxLength { get; internal set; }

    /// <summary>The regular expression.</summary>
    public string? Pattern { get; internal set; }

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

    /// <summary>The minimum item count.</summary>
    public int? MinItems { get; internal set; }

    /// <summary>The maximum item count.</summary>
    public int? MaxItems { get; internal set; }

    /// <summary>Whether any constraint is set.</summary>
    public bool IsEmpty =>
        MinLength is null && MaxLength is null && Pattern is null && Minimum is null && Maximum is null &&
        ExclusiveMinimum is null && ExclusiveMaximum is null && MultipleOf is null && MinItems is null && MaxItems is null;

    internal static ApiConstraints From(OpenApiSchema schema)
    {
        var constraints = new ApiConstraints
        {
            MinLength = schema.MinLength,
            MaxLength = schema.MaxLength,
            Pattern = schema.Pattern,
            Minimum = schema.Minimum,
            Maximum = schema.Maximum,
            ExclusiveMinimum = schema.ExclusiveMinimum,
            ExclusiveMaximum = schema.ExclusiveMaximum,
            MultipleOf = schema.MultipleOf,
            MinItems = schema.MinItems,
            MaxItems = schema.MaxItems,
        };
        return constraints.IsEmpty ? None : constraints;
    }
}

/// <summary>A property of an object type.</summary>
public sealed class ApiProperty
{
    internal ApiProperty(string jsonName, ApiTypeRef type, OpenApiSchema schema)
    {
        JsonName = jsonName;
        Type = type;
        Schema = schema;
    }

    /// <summary>The property name in JSON.</summary>
    public string JsonName { get; }

    /// <summary>The property type; <see cref="ApiTypeRef.IsNullable"/> says whether JSON <c>null</c> is allowed.</summary>
    public ApiTypeRef Type { get; internal set; }

    /// <summary>The property schema.</summary>
    public OpenApiSchema Schema { get; }

    /// <summary>
    /// Whether the property must be present in JSON (listed in <c>required</c>). Independent of nullability: a
    /// required property may be null when its type is nullable, and an optional one may be absent.
    /// </summary>
    public bool Required { get; internal set; }

    /// <summary>Whether the property is only sent by the server.</summary>
    public bool ReadOnly { get; internal set; }

    /// <summary>Whether the property is only sent by the client.</summary>
    public bool WriteOnly { get; internal set; }

    /// <summary>Whether the property is deprecated.</summary>
    public bool Deprecated { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }

    /// <summary>The default value.</summary>
    public DocNode? Default { get; internal set; }

    /// <summary>An example value.</summary>
    public DocNode? Example { get; internal set; }

    /// <summary>Validation constraints.</summary>
    public ApiConstraints Constraints { get; internal set; } = ApiConstraints.None;

    /// <summary>Whether this property is the discriminator of a polymorphic hierarchy or union.</summary>
    public bool IsDiscriminator { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"{JsonName}{(Required ? string.Empty : "?")}: {Type}";
}

/// <summary>One mapping of a discriminator value to a type.</summary>
public sealed class ApiDiscriminatorMapping
{
    internal ApiDiscriminatorMapping(string value, ApiNamedType type)
    {
        Value = value;
        Type = type;
    }

    /// <summary>The discriminator value.</summary>
    public string Value { get; }

    /// <summary>The type it selects.</summary>
    public ApiNamedType Type { get; }
}

/// <summary>A discriminator: the property that selects the concrete type and the value mappings.</summary>
public sealed class ApiDiscriminator
{
    internal ApiDiscriminator(string propertyName) => PropertyName = propertyName;

    /// <summary>The JSON property name.</summary>
    public string PropertyName { get; }

    /// <summary>The mappings, in a deterministic order (explicit mappings first, then implicit ones).</summary>
    public IReadOnlyList<ApiDiscriminatorMapping> Mappings { get; internal set; } = [];
}

/// <summary>An object type with properties, optionally part of an inheritance hierarchy.</summary>
public sealed class ApiObjectType : ApiNamedType
{
    internal ApiObjectType(IReadOnlyList<string> nameHint, ApiTypeOrigin origin, OpenApiSchema schema)
        : base(nameHint, origin, schema)
    {
    }

    /// <summary>The properties declared by this type (inherited properties are on <see cref="BaseType"/>).</summary>
    public IReadOnlyList<ApiProperty> Properties { get; internal set; } = [];

    /// <summary>The base type (from an <c>allOf</c> reference), when this type extends another.</summary>
    public ApiObjectType? BaseType { get; internal set; }

    /// <summary>The types that extend this one, in a deterministic order.</summary>
    public IReadOnlyList<ApiObjectType> DerivedTypes { get; internal set; } = [];

    /// <summary>The discriminator, when this type is the root of a polymorphic hierarchy.</summary>
    public ApiDiscriminator? Discriminator { get; internal set; }

    /// <summary>The value of the inherited discriminator that selects this type, when it is part of a hierarchy.</summary>
    public string? DiscriminatorValue { get; internal set; }

    /// <summary>
    /// The type of additional (undeclared) properties: <see langword="null"/> when they are not modeled. Free-form
    /// additional properties are <see cref="ApiPrimitiveKind.Any"/>.
    /// </summary>
    public ApiTypeRef? AdditionalProperties { get; internal set; }

    /// <summary>Whether additional properties are forbidden (<c>additionalProperties: false</c>).</summary>
    public bool AdditionalPropertiesForbidden { get; internal set; }

    /// <summary>All properties including inherited ones, base-most first.</summary>
    public IEnumerable<ApiProperty> AllProperties =>
        (BaseType?.AllProperties ?? []).Concat(Properties);

    /// <summary>The root of the inheritance chain (this type when it has no base).</summary>
    public ApiObjectType Root => BaseType?.Root ?? this;
}

/// <summary>One member of an enum type.</summary>
public sealed class ApiEnumMember
{
    internal ApiEnumMember(DocNode value, string? nameHint, string? description)
    {
        Value = value;
        NameHint = nameHint;
        Description = description;
    }

    /// <summary>The JSON value.</summary>
    public DocNode Value { get; }

    /// <summary>The value as text (the string itself, or the number's JSON form).</summary>
    public string ValueText => Value is ScalarNode scalar ? scalar.Value : Value.ToJson();

    /// <summary>An explicit member name (<c>x-enum-varnames</c>, <c>x-enumNames</c>), when provided.</summary>
    public string? NameHint { get; }

    /// <summary>A description (<c>x-enum-descriptions</c>), when provided.</summary>
    public string? Description { get; }
}

/// <summary>An enum type.</summary>
public sealed class ApiEnumType : ApiNamedType
{
    internal ApiEnumType(IReadOnlyList<string> nameHint, ApiTypeOrigin origin, OpenApiSchema schema, ApiPrimitiveKind underlyingKind)
        : base(nameHint, origin, schema) => UnderlyingKind = underlyingKind;

    /// <summary>The kind of the values: <see cref="ApiPrimitiveKind.String"/> or <see cref="ApiPrimitiveKind.Integer"/>.</summary>
    public ApiPrimitiveKind UnderlyingKind { get; }

    /// <summary>The integer format, for integer enums.</summary>
    public string? Format { get; internal set; }

    /// <summary>The members in document order (a <c>null</c> entry is represented by nullability instead).</summary>
    public IReadOnlyList<ApiEnumMember> Members { get; internal set; } = [];
}

/// <summary>How a union was declared.</summary>
public enum ApiUnionKind
{
    /// <summary><c>oneOf</c>: exactly one variant matches.</summary>
    OneOf,

    /// <summary><c>anyOf</c>: one or more variants match.</summary>
    AnyOf,
}

/// <summary>One variant of a union.</summary>
public sealed class ApiUnionVariant
{
    internal ApiUnionVariant(ApiTypeRef type, string? discriminatorValue)
    {
        Type = type;
        DiscriminatorValue = discriminatorValue;
    }

    /// <summary>The variant type.</summary>
    public ApiTypeRef Type { get; }

    /// <summary>The discriminator value selecting this variant, when the union has a discriminator.</summary>
    public string? DiscriminatorValue { get; internal set; }
}

/// <summary>A union (<c>oneOf</c>/<c>anyOf</c>) of several types.</summary>
public sealed class ApiUnionType : ApiNamedType
{
    internal ApiUnionType(IReadOnlyList<string> nameHint, ApiTypeOrigin origin, OpenApiSchema schema, ApiUnionKind kind)
        : base(nameHint, origin, schema) => Kind = kind;

    /// <summary>How the union was declared.</summary>
    public ApiUnionKind Kind { get; }

    /// <summary>The variants in document order.</summary>
    public IReadOnlyList<ApiUnionVariant> Variants { get; internal set; } = [];

    /// <summary>The discriminator property name, when the variants are told apart by a property value.</summary>
    public string? DiscriminatorPropertyName { get; internal set; }

    /// <summary>
    /// How variants can be told apart at runtime: by discriminator, by JSON value kind (string vs number vs object
    /// ...), or not reliably (<see cref="ApiUnionDispatch.Ambiguous"/>).
    /// </summary>
    public ApiUnionDispatch Dispatch { get; internal set; }
}

/// <summary>How a union's variants are told apart.</summary>
public enum ApiUnionDispatch
{
    /// <summary>By the value of a discriminator property.</summary>
    Discriminator,

    /// <summary>By the JSON value kind; every variant has a distinct kind.</summary>
    ValueKind,

    /// <summary>The variants cannot be told apart reliably.</summary>
    Ambiguous,
}
