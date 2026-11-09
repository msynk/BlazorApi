using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>How a C# type is used, which decides collection and binary representations.</summary>
internal enum TypeUse
{
    /// <summary>A model property, array item or map value.</summary>
    Model,

    /// <summary>A method parameter (collections accept any enumerable).</summary>
    Parameter,
}

/// <summary>The polymorphic serialization plan of a discriminated hierarchy root (allOf inheritance or a union base).</summary>
internal sealed class PolymorphicPlan
{
    public PolymorphicPlan(ApiNamedType root, string discriminatorProperty)
    {
        Root = root;
        DiscriminatorProperty = discriminatorProperty;
    }

    public ApiNamedType Root { get; }

    public string DiscriminatorProperty { get; }

    public string ConverterName { get; set; } = string.Empty;

    public string UnrecognizedName { get; set; } = string.Empty;

    /// <summary>Concrete types with their discriminator values, most derived first.</summary>
    public List<(string Value, ApiObjectType Type)> Mappings { get; } = [];

    /// <summary>The root's declared discriminator property (allOf roots only), set by derived constructors.</summary>
    public ApiProperty? RootProperty { get; set; }
}

/// <summary>The wrapper plan of a union that cannot be expressed with inheritance.</summary>
internal sealed class UnionWrapperPlan
{
    public UnionWrapperPlan(ApiUnionType union) => Union = union;

    public ApiUnionType Union { get; }

    public string ConverterName { get; set; } = string.Empty;

    public List<(ApiUnionVariant Variant, string Name)> Variants { get; } = [];
}

/// <summary>A generated client class.</summary>
internal sealed class ClientPlan
{
    public ClientPlan(string className, string? tag)
    {
        ClassName = className;
        InterfaceName = "I" + className;
        Tag = tag;
    }

    public string ClassName { get; }

    public string InterfaceName { get; }

    public string? Tag { get; }

    public List<OperationPlan> Operations { get; } = [];

    public NameScope Members { get; } = new();
}

/// <summary>A generated operation method pair.</summary>
internal sealed class OperationPlan
{
    public OperationPlan(ApiOperation operation, string name)
    {
        Operation = operation;
        Name = name;
    }

    public ApiOperation Operation { get; }

    /// <summary>The method name without the Async suffix.</summary>
    public string Name { get; }

    public string Id => Operation.OperationId ?? Operation.Method.ToLowerInvariant() + " " + Operation.Path;
}

/// <summary>Decisions shared by every emitter: selected operations, emitted types, names and type mapping.</summary>
internal sealed class GenerationPlan
{
    private readonly Dictionary<ApiNamedType, string> _typeNames = new(IdentityComparer.Instance);

    public GenerationPlan(ApiModel model, GeneratorOptions options, DiagnosticBag diagnostics)
    {
        Model = model;
        Options = options;
        Diagnostics = diagnostics;
        Accessibility = options.InternalTypes ? "internal" : "public";
    }

    public ApiModel Model { get; }

    public GeneratorOptions Options { get; }

    public DiagnosticBag Diagnostics { get; }

    public string Accessibility { get; }

    public List<ClientPlan> Clients { get; } = [];

    public List<ApiNamedType> Types { get; } = [];

    public Dictionary<ApiNamedType, PolymorphicPlan> Polymorphic { get; } = new(IdentityComparer.Instance);

    public Dictionary<ApiUnionType, UnionWrapperPlan> Wrappers { get; } = new(IdentityComparer.Instance);

    /// <summary>Union bases that variants inherit from (discriminated unions of exclusive object variants).</summary>
    public Dictionary<ApiObjectType, ApiUnionType> UnionBase { get; } = new(IdentityComparer.Instance);

    /// <summary>Types used as JSON merge-patch bodies (optional nullable properties become Optional&lt;T&gt;).</summary>
    public HashSet<ApiNamedType> MergePatchTypes { get; } = new(IdentityComparer.Instance);

    /// <summary>Inline types emitted as method parameters instead of classes (inline multipart and form bodies).</summary>
    public HashSet<ApiNamedType> ParameterBodies { get; } = new(IdentityComparer.Instance);

    public NameScope TypeScope { get; } = new();

    public string Namespace => Options.Namespace;

    public string ModelsNamespace => Options.EffectiveModelsNamespace;

    public string TypeName(ApiNamedType type) => _typeNames[type];

    public bool IsEmitted(ApiNamedType type) => _typeNames.ContainsKey(type);

    public void SetTypeName(ApiNamedType type, string name) => _typeNames[type] = name;

    /// <summary>The C# base type of an object (its allOf base or the union it is a variant of).</summary>
    public ApiNamedType? CSharpBase(ApiObjectType type) =>
        type.BaseType is { } baseType ? baseType : UnionBase.TryGetValue(type, out var union) ? union : null;

    /// <summary>The polymorphic plan whose hierarchy contains the type, if any.</summary>
    public PolymorphicPlan? PolymorphicRootOf(ApiObjectType type)
    {
        for (ApiNamedType? current = type; current is not null; current = current is ApiObjectType o ? CSharpBase(o) : null)
        {
            if (Polymorphic.TryGetValue(current, out var plan))
            {
                return plan;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ type mapping

    /// <summary>The C# type of an API type reference.</summary>
    public string CSharpType(ApiTypeRef type, TypeUse use = TypeUse.Model, bool? nullable = null)
    {
        var isNullable = nullable ?? type.IsNullable;
        var core = type switch
        {
            ApiPrimitiveType p => Primitive(p),
            ApiArrayType a => Collection(a, use),
            ApiMapType m => Map(m, use),
            ApiNamedTypeRef n => NamedType(n.Target),
            _ => "object",
        };
        return isNullable ? core + "?" : core;
    }

    public string NamedType(ApiNamedType type)
    {
        if (!IsEmitted(type))
        {
            return "global::System.Text.Json.JsonElement";
        }

        var name = TypeName(type);
        return ModelsNamespace == Namespace ? name : "global::" + ModelsNamespace + "." + name;
    }

    private string Collection(ApiArrayType array, TypeUse use)
    {
        var item = CSharpType(array.Items, TypeUse.Model);
        if (use == TypeUse.Parameter)
        {
            return $"global::System.Collections.Generic.IEnumerable<{item}>";
        }

        return Options.Collections switch
        {
            CollectionStyle.Mutable => $"global::System.Collections.Generic.List<{item}>",
            CollectionStyle.Array => item + "[]",
            _ => $"global::System.Collections.Generic.IReadOnlyList<{item}>",
        };
    }

    private string Map(ApiMapType map, TypeUse use)
    {
        var value = CSharpType(map.Values, TypeUse.Model);
        return Options.Collections == CollectionStyle.ReadOnly || use == TypeUse.Parameter
            ? $"global::System.Collections.Generic.IReadOnlyDictionary<string, {value}>"
            : $"global::System.Collections.Generic.Dictionary<string, {value}>";
    }

    public string Primitive(ApiPrimitiveType type) => type.Kind switch
    {
        ApiPrimitiveKind.String => "string",
        ApiPrimitiveKind.Boolean => "bool",
        ApiPrimitiveKind.Integer => type.Format switch
        {
            "int32" => "int",
            "int64" => "long",
            "int8" => "sbyte",
            "int16" => "short",
            "uint8" => "byte",
            "uint16" => "ushort",
            "uint32" => "uint",
            "uint64" => "ulong",
            _ => Options.IntegerWithoutFormat == IntegerType.Int32 ? "int" : "long",
        },
        ApiPrimitiveKind.Number => type.Format switch
        {
            "float" => "float",
            "double" => "double",
            "decimal" or "decimal128" => "decimal",
            _ => Options.NumberWithoutFormat == NumberType.Decimal ? "decimal" : "double",
        },
        ApiPrimitiveKind.Date => Options.DateType switch
        {
            DateType.DateTimeOffset => "global::System.DateTimeOffset",
            DateType.String => "string",
            _ => "global::System.DateOnly",
        },
        ApiPrimitiveKind.DateTime => Options.DateTimeType == DateTimeType.DateTime ? "global::System.DateTime" : "global::System.DateTimeOffset",
        ApiPrimitiveKind.Time => Options.TimeType == TimeType.TimeOnly ? "global::System.TimeOnly" : "string",
        ApiPrimitiveKind.Duration => "string",
        ApiPrimitiveKind.Uuid => "global::System.Guid",
        ApiPrimitiveKind.Uri => "global::System.Uri",
        ApiPrimitiveKind.Base64 => "byte[]",
        ApiPrimitiveKind.Binary => "global::BlazorApi.Runtime.FileContent",
        _ => "global::System.Text.Json.JsonElement",
    };

    /// <summary>Whether the C# type of a reference is a value type (so <c>T?</c> is <c>Nullable&lt;T&gt;</c>).</summary>
    public bool IsValueType(ApiTypeRef type) => type switch
    {
        ApiPrimitiveType p => Primitive(p) is not ("string" or "byte[]" or "global::System.Uri" or "global::BlazorApi.Runtime.FileContent"),
        ApiNamedTypeRef { Target: ApiEnumType } n => IsEmitted(n.Target),
        ApiNamedTypeRef n => !IsEmitted(n.Target),
        _ => false,
    };

    /// <summary>
    /// An expression that formats a non-null value of <paramref name="type"/> as its wire string (for parameters
    /// and form fields).
    /// </summary>
    public string FormatScalar(ApiTypeRef type, string expression, string jsonOptions)
    {
        switch (type)
        {
            case ApiPrimitiveType p:
                var csharp = Primitive(p);
                return csharp switch
                {
                    "string" => expression,
                    "global::System.Text.Json.JsonElement" => expression + ".ToString()",
                    "global::BlazorApi.Runtime.FileContent" => "\"\"",
                    _ => $"global::BlazorApi.Runtime.Http.ApiFormat.Value({expression})",
                };
            case ApiNamedTypeRef { Target: ApiEnumType e } when IsEmitted(e):
                return $"{NamedType(e)}JsonConverter.ToWireValue({expression})";
            default:
                return $"global::BlazorApi.Runtime.Json.ApiJson.Serialize({expression}, {jsonOptions})";
        }
    }

    /// <summary>
    /// An expression producing a <c>ParameterValue</c> from a non-null value of <paramref name="type"/>.
    /// </summary>
    public string ParameterValue(ApiTypeRef type, string expression, string jsonOptions)
    {
        const string pv = "global::BlazorApi.Runtime.Http.ParameterValue";
        switch (type)
        {
            case ApiArrayType array when array.Items is ApiPrimitiveType or ApiNamedTypeRef { Target: ApiEnumType }:
                var item = array.Items.IsNullable && IsValueType(array.Items) ? "item.GetValueOrDefault()" : "item!";
                return $"{pv}.Array(global::System.Linq.Enumerable.Select({expression}, item => {FormatScalar(array.Items.WithNullable(false), item, jsonOptions)}))";
            case ApiArrayType:
                return $"{pv}.Array(global::System.Linq.Enumerable.Select({expression}, item => global::BlazorApi.Runtime.Json.ApiJson.Serialize(item, {jsonOptions})))";
            case ApiMapType map:
                var value = map.Values.IsNullable && IsValueType(map.Values) ? "pair.Value.GetValueOrDefault()" : "pair.Value!";
                return $"{pv}.Object(global::System.Linq.Enumerable.Select({expression}, pair => new global::System.Collections.Generic.KeyValuePair<string, string?>(pair.Key, {FormatScalar(map.Values.WithNullable(false), value, jsonOptions)})))";
            case ApiNamedTypeRef { Target: ApiObjectType obj } when IsEmitted(obj):
                var pairs = new List<string>();
                foreach (var property in obj.AllProperties)
                {
                    pairs.Add($"new global::System.Collections.Generic.KeyValuePair<string, string?>({Literal.String(property.JsonName)}, {FormatNullable(property, $"{expression}.{PropertyName(obj, property)}", jsonOptions, "p" + pairs.Count + "_")})");
                }

                return $"{pv}.Object(new global::System.Collections.Generic.KeyValuePair<string, string?>[] {{ {string.Join(", ", pairs)} }})";
            default:
                return $"{pv}.Scalar({FormatScalar(type.WithNullable(false), expression, jsonOptions)})";
        }
    }

    /// <summary>Formats a model property that may be null or absent: <c>x is {} v ? Format(v) : null</c>.</summary>
    public string FormatNullable(ApiProperty property, string access, string jsonOptions, string variable = "v_")
    {
        var shape = PropertyShape(property);
        if (shape.IsOptionalWrapper)
        {
            access += ".GetValueOrDefault(default!)";
        }

        var type = property.Type.WithNullable(false);
        if (type is ApiArrayType or ApiMapType or ApiNamedTypeRef { Target: ApiObjectType or ApiUnionType })
        {
            return $"{access} is {{ }} {variable} ? global::BlazorApi.Runtime.Json.ApiJson.Serialize({variable}, {jsonOptions}) : null";
        }

        return shape.CSharpNullable
            ? $"{access} is {{ }} {variable} ? {FormatScalar(type, variable, jsonOptions)} : null"
            : FormatScalar(type, access, jsonOptions);
    }

    // ------------------------------------------------------------------ enums

    private readonly Dictionary<ApiEnumType, IReadOnlyList<(ApiEnumMember Member, string Name)>> _enumMembers = new(IdentityComparer.Instance);

    /// <summary>The C# member names of an enum, in declaration order.</summary>
    public IReadOnlyList<(ApiEnumMember Member, string Name)> EnumMembers(ApiEnumType type)
    {
        if (!_enumMembers.TryGetValue(type, out var members))
        {
            var scope = new NameScope(reserved: [TypeName(type)]);
            members = type.Members
                .Select(m => (m, scope.Allocate(m.NameHint is { Length: > 0 } hint ? Identifier.Pascal(hint) : Identifier.EnumMember(m.ValueText))))
                .ToList();
            _enumMembers[type] = members;
        }

        return members;
    }

    // ------------------------------------------------------------------ properties

    private readonly Dictionary<ApiObjectType, Dictionary<ApiProperty, string>> _propertyNames = new(IdentityComparer.Instance);
    private readonly Dictionary<ApiObjectType, NameScope> _memberScopes = new(IdentityComparer.Instance);

    /// <summary>The member scope of an object type (its own and inherited member names are reserved).</summary>
    public NameScope MemberScope(ApiObjectType type)
    {
        if (_memberScopes.TryGetValue(type, out var scope))
        {
            return scope;
        }

        scope = new NameScope(reserved: [TypeName(type), "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize"]);
        _memberScopes[type] = scope;
        var map = new Dictionary<ApiProperty, string>(IdentityComparer.Instance);
        _propertyNames[type] = map;
        if (CSharpBase(type) is ApiObjectType baseType && IsEmitted(baseType))
        {
            foreach (var inherited in baseType.AllProperties)
            {
                scope.Reserve(PropertyName(baseType, inherited));
            }
        }

        foreach (var property in type.Properties)
        {
            map[property] = scope.Allocate(Identifier.Pascal(property.JsonName));
        }

        return scope;
    }

    /// <summary>The C# name of a property, looked up on the type that declares it.</summary>
    public string PropertyName(ApiObjectType type, ApiProperty property)
    {
        for (ApiObjectType? current = type; current is not null; current = current.BaseType)
        {
            MemberScope(current);
            if (_propertyNames[current].TryGetValue(property, out var name))
            {
                return name;
            }
        }

        return Identifier.Pascal(property.JsonName);
    }

    /// <summary>How a property is declared.</summary>
    public PropertyShape PropertyShape(ApiProperty property, ApiObjectType? owner = null)
    {
        var constant = property.Type is ApiPrimitiveType { ConstValue: not null };
        var conditional = property.ReadOnly || property.WriteOnly;
        var required = property.Required && !conditional && !constant;
        var isDiscriminator = owner is not null && Polymorphic.TryGetValue(owner, out var plan) && plan.RootProperty == property;
        if (isDiscriminator)
        {
            required = false;
        }

        var optionalWrapper = !required && !constant && property.Type.IsNullable && owner is not null &&
            (Options.OptionalNullable == OptionalNullableStyle.Optional ||
             (Options.OptionalNullable == OptionalNullableStyle.Auto && MergePatchTypes.Contains(owner)));
        var csharpNullable = optionalWrapper ? property.Type.IsNullable : property.Type.IsNullable || (!required && !constant && !isDiscriminator);
        return new PropertyShape(required, csharpNullable, optionalWrapper, constant, isDiscriminator);
    }
}

/// <summary>How a model property is declared in C#.</summary>
internal readonly struct PropertyShape
{
    public PropertyShape(bool required, bool csharpNullable, bool isOptionalWrapper, bool isConstant, bool isDiscriminator)
    {
        Required = required;
        CSharpNullable = csharpNullable;
        IsOptionalWrapper = isOptionalWrapper;
        IsConstant = isConstant;
        IsDiscriminator = isDiscriminator;
    }

    /// <summary>The property must be present (C# <c>required</c> when enabled).</summary>
    public bool Required { get; }

    /// <summary>The C# property type is nullable.</summary>
    public bool CSharpNullable { get; }

    /// <summary>The property is wrapped in <c>Optional&lt;T&gt;</c>.</summary>
    public bool IsOptionalWrapper { get; }

    /// <summary>The property has a constant value (initialized, always written).</summary>
    public bool IsConstant { get; }

    /// <summary>The property is a hierarchy discriminator set by constructors.</summary>
    public bool IsDiscriminator { get; }
}

internal sealed class IdentityComparer : IEqualityComparer<object>
{
    public static readonly IdentityComparer Instance = new();

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
