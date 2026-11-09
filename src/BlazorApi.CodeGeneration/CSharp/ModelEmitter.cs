using System.Globalization;
using System.Text.RegularExpressions;
using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Model;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>Emits model types: classes, enums, polymorphic converters and union wrappers.</summary>
internal sealed class ModelEmitter
{
    private const string Json = "global::System.Text.Json";
    private const string Serialization = "global::System.Text.Json.Serialization";
    private const string ApiJson = "global::BlazorApi.Runtime.Json.ApiJson";
    private const string Annotations = "global::System.ComponentModel.DataAnnotations";

    private readonly GenerationPlan _plan;

    public ModelEmitter(GenerationPlan plan) => _plan = plan;

    public IEnumerable<GeneratedFile> Emit()
    {
        foreach (var type in _plan.Types)
        {
            var writer = FileWriter.Start(_plan, _plan.ModelsNamespace);
            switch (type)
            {
                case ApiObjectType obj:
                    EmitObject(writer, obj);
                    break;
                case ApiEnumType enumType:
                    EmitEnum(writer, enumType);
                    break;
                case ApiUnionType union when _plan.Polymorphic.ContainsKey(union):
                    EmitUnionBase(writer, union);
                    break;
                case ApiUnionType union:
                    EmitUnionWrapper(writer, union, _plan.Wrappers[union]);
                    break;
            }

            yield return new GeneratedFile("Models/" + _plan.TypeName(type) + ".g.cs", writer.ToString());
        }
    }

    // ------------------------------------------------------------------ objects

    private void EmitObject(CodeWriter writer, ApiObjectType type)
    {
        var name = _plan.TypeName(type);
        _plan.Polymorphic.TryGetValue(type, out var polymorphic);
        WriteTypeDocs(writer, type);
        if (polymorphic is not null)
        {
            writer.Line($"[{Serialization}.JsonConverter(typeof({polymorphic.ConverterName}))]");
        }

        var baseType = _plan.CSharpBase(type);
        var modifiers = polymorphic is not null ? "abstract partial" : "partial";
        var inheritance = baseType is not null && _plan.IsEmitted(baseType) ? " : " + _plan.TypeName(baseType) : string.Empty;
        using (writer.Block($"{_plan.Accessibility} {modifiers} class {name}{inheritance}"))
        {
            var members = new List<Action>();
            if (_plan.PolymorphicRootOf(type) is { RootProperty: { } discriminator } root && !ReferenceEquals(root.Root, type) &&
                root.Mappings.FirstOrDefault(m => ReferenceEquals(m.Type, type)) is { Type: not null } mapping)
            {
                members.Add(() =>
                {
                    writer.Doc("summary", $"Creates a {name} with its discriminator set to {Literal.String(mapping.Value)}.");
                    using (writer.Block($"public {name}()"))
                    {
                        writer.Line($"{_plan.PropertyName((ApiObjectType)root.Root, discriminator)} = {DiscriminatorLiteral(discriminator, mapping.Value)};");
                    }
                });
            }

            foreach (var property in type.Properties)
            {
                members.Add(() => EmitProperty(writer, type, property));
            }

            if (type.AdditionalProperties is not null && !HasExtensionDataInBase(type))
            {
                var propertyName = _plan.MemberScope(type).Allocate("AdditionalProperties");
                members.Add(() => EmitExtensionData(writer, propertyName, $"Properties not declared by {name}, keyed by name" +
                    (type.AdditionalProperties is ApiPrimitiveType { Kind: ApiPrimitiveKind.Any } ? "." : $" (values are {Describe(type.AdditionalProperties)})."))
                );
            }

            WriteMembers(writer, members);
        }

        if (polymorphic is not null)
        {
            writer.Line();
            EmitUnrecognized(writer, type, polymorphic);
            writer.Line();
            EmitPolymorphicConverter(writer, type, polymorphic);
        }
    }

    private void EmitProperty(CodeWriter writer, ApiObjectType owner, ApiProperty property)
    {
        var shape = _plan.PropertyShape(property, owner);
        var name = _plan.PropertyName(owner, property);
        var valueType = _plan.CSharpType(property.Type, TypeUse.Model, nullable: shape.CSharpNullable && !shape.IsOptionalWrapper ? true : property.Type.IsNullable);
        var type = shape.IsOptionalWrapper ? $"global::BlazorApi.Runtime.Json.Optional<{valueType}>" : valueType;

        if (_plan.Options.GenerateDocumentation)
        {
            writer.Doc("summary", property.Description ?? DefaultPropertySummary(property, shape));
            var remarks = new List<string>();
            if (property.ReadOnly)
            {
                remarks.Add("Read-only: sent by the server only, so it is nullable and omitted from requests.");
            }

            if (property.WriteOnly)
            {
                remarks.Add("Write-only: never returned by the server.");
            }

            if (property.Default is { } defaultValue)
            {
                remarks.Add($"Default: {defaultValue.ToJson()}.");
            }

            if (shape.IsOptionalWrapper)
            {
                remarks.Add("Unset values are omitted from the JSON; a value set to null is sent as null.");
            }

            if (remarks.Count > 0)
            {
                writer.Doc("remarks", string.Join("\n", remarks));
            }
        }

        writer.Line($"[{Serialization}.JsonPropertyName({Literal.String(property.JsonName)})]");
        if (shape.IsOptionalWrapper)
        {
            writer.Line($"[{Serialization}.JsonIgnore(Condition = {Serialization}.JsonIgnoreCondition.WhenWritingDefault)]");
            writer.Line($"[{Serialization}.JsonConverter(typeof(global::BlazorApi.Runtime.Json.OptionalJsonConverter<{valueType}>))]");
        }
        else if (!shape.Required && !shape.IsConstant && !shape.IsDiscriminator && shape.CSharpNullable && !property.Required)
        {
            writer.Line($"[{Serialization}.JsonIgnore(Condition = {Serialization}.JsonIgnoreCondition.WhenWritingNull)]");
        }
        else if (!shape.Required && !shape.IsConstant && !shape.IsDiscriminator && (property.ReadOnly || property.WriteOnly))
        {
            writer.Line($"[{Serialization}.JsonIgnore(Condition = {Serialization}.JsonIgnoreCondition.WhenWritingNull)]");
        }

        if (_plan.Options.GenerateDataAnnotations)
        {
            foreach (var annotation in DataAnnotations(property, shape))
            {
                writer.Line(annotation);
            }
        }

        if (property.Deprecated)
        {
            writer.Line("[global::System.Obsolete]");
        }

        var required = shape.Required && _plan.Options.UseRequiredMembers ? "required " : string.Empty;
        var initializer = string.Empty;
        if (shape.IsConstant && property.Type is ApiPrimitiveType { ConstValue: { } constant })
        {
            initializer = " = " + ConstantLiteral(constant, valueType) + ";";
        }
        else if (shape.IsDiscriminator)
        {
            initializer = property.Type is ApiNamedTypeRef ? " = default;" : " = string.Empty;";
        }
        else if (shape.Required && !_plan.Options.UseRequiredMembers && !shape.CSharpNullable && !_plan.IsValueType(property.Type))
        {
            initializer = " = default!;";
        }

        writer.Line($"public {required}{type} {name} {{ get; set; }}{initializer}");
    }

    private static string DefaultPropertySummary(ApiProperty property, PropertyShape shape) =>
        $"The '{property.JsonName}' property" + (shape.Required ? " (required)." : ".");

    private IEnumerable<string> DataAnnotations(ApiProperty property, PropertyShape shape)
    {
        var constraints = property.Constraints;
        if (shape.Required && !_plan.IsValueType(property.Type) && !property.Type.IsNullable)
        {
            yield return $"[{Annotations}.Required(AllowEmptyStrings = true)]";
        }

        var isString = property.Type is ApiPrimitiveType { Kind: ApiPrimitiveKind.String } && _plan.CSharpType(property.Type, nullable: false) == "string";
        if (isString)
        {
            if (constraints.MaxLength is { } max)
            {
                yield return constraints.MinLength is { } min and > 0
                    ? $"[{Annotations}.StringLength({max}, MinimumLength = {min})]"
                    : $"[{Annotations}.StringLength({max})]";
            }
            else if (constraints.MinLength is { } min and > 0)
            {
                yield return $"[{Annotations}.MinLength({min})]";
            }

            if (constraints.Pattern is { } pattern && IsValidRegex(pattern))
            {
                yield return $"[{Annotations}.RegularExpression({Literal.String(pattern)})]";
            }
        }

        if (property.Type is ApiArrayType)
        {
            if (constraints.MinItems is { } minItems and > 0)
            {
                yield return $"[{Annotations}.MinLength({minItems})]";
            }

            if (constraints.MaxItems is { } maxItems)
            {
                yield return $"[{Annotations}.MaxLength({maxItems})]";
            }
        }

        if (property.Type is ApiPrimitiveType { Kind: ApiPrimitiveKind.Integer or ApiPrimitiveKind.Number })
        {
            var minimum = constraints.Minimum ?? constraints.ExclusiveMinimum;
            var maximum = constraints.Maximum ?? constraints.ExclusiveMaximum;
            if (minimum is not null || maximum is not null)
            {
                var range = $"[{Annotations}.Range({Number(minimum, "double.MinValue")}, {Number(maximum, "double.MaxValue")}";
                if (constraints.ExclusiveMinimum is not null && constraints.Minimum is null)
                {
                    range += ", MinimumIsExclusive = true";
                }

                if (constraints.ExclusiveMaximum is not null && constraints.Maximum is null)
                {
                    range += ", MaximumIsExclusive = true";
                }

                yield return range + ")]";
            }
        }

        static string Number(decimal? value, string fallback) =>
            value is null ? fallback : value.Value.ToString(CultureInfo.InvariantCulture) + "d";
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void EmitExtensionData(CodeWriter writer, string propertyName, string summary)
    {
        writer.Doc("summary", summary);
        writer.Line($"[{Serialization}.JsonExtensionData]");
        writer.Line($"public global::System.Collections.Generic.IDictionary<string, {Json}.JsonElement>? {propertyName} {{ get; set; }}");
    }

    private bool HasExtensionDataInBase(ApiObjectType type)
    {
        for (var current = _plan.CSharpBase(type) as ApiObjectType; current is not null; current = _plan.CSharpBase(current) as ApiObjectType)
        {
            if (current.AdditionalProperties is not null)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ polymorphism

    private void EmitUnrecognized(CodeWriter writer, ApiNamedType root, PolymorphicPlan plan)
    {
        var rootName = _plan.TypeName(root);
        writer.Doc("summary", $"A {rootName} whose discriminator value ('{plan.DiscriminatorProperty}') this client does not recognize. Its undeclared properties are kept in AdditionalProperties.");
        using (writer.Block($"{_plan.Accessibility} sealed partial class {plan.UnrecognizedName} : {rootName}"))
        {
            var hasExtension = root is ApiObjectType obj && (obj.AdditionalProperties is not null || HasExtensionDataInBase(obj));
            if (!hasExtension)
            {
                var propertyName = root is ApiObjectType o && o.AllProperties.Any(p => _plan.PropertyName(o, p) == "AdditionalProperties") ? "UnrecognizedProperties" : "AdditionalProperties";
                EmitExtensionData(writer, propertyName, "The properties of the unrecognized value that its base type does not declare.");
            }
        }
    }

    private void EmitPolymorphicConverter(CodeWriter writer, ApiNamedType root, PolymorphicPlan plan)
    {
        var rootName = _plan.TypeName(root);
        writer.Doc("summary", $"Reads and writes {rootName} values as their concrete type, selected by the '{plan.DiscriminatorProperty}' property.");
        using (writer.Block($"{_plan.Accessibility} sealed class {plan.ConverterName} : {Serialization}.JsonConverter<{rootName}>"))
        {
            writer.Doc("summary", "Reads the concrete type named by the discriminator; unknown values become the unrecognized type.");
            using (writer.Block($"public override {rootName}? Read(ref {Json}.Utf8JsonReader reader, global::System.Type typeToConvert, {Json}.JsonSerializerOptions options)"))
            {
                using (writer.Block($"if (reader.TokenType == {Json}.JsonTokenType.Null)"))
                {
                    writer.Line("return null;");
                }

                writer.Line();
                using (writer.Block($"return {ApiJson}.PeekDiscriminator(reader, {Literal.String(plan.DiscriminatorProperty)}) switch", "};"))
                {
                    foreach (var mapping in plan.Mappings.GroupBy(m => m.Value).Select(g => g.First()))
                    {
                        writer.Line($"{Literal.String(mapping.Value)} => {ApiJson}.Read<{_plan.TypeName(mapping.Type)}>(ref reader, options),");
                    }

                    writer.Line($"_ => {ApiJson}.Read<{plan.UnrecognizedName}>(ref reader, options),");
                }
            }

            writer.Line();
            writer.Doc("summary", "Writes the value as its runtime type.");
            using (writer.Block($"public override void Write({Json}.Utf8JsonWriter writer, {rootName} value, {Json}.JsonSerializerOptions options)"))
            {
                using (writer.Block("switch (value)"))
                {
                    var written = new HashSet<ApiObjectType>(IdentityComparer.Instance);
                    foreach (var mapping in plan.Mappings)
                    {
                        if (!written.Add(mapping.Type))
                        {
                            continue;
                        }

                        var typeName = _plan.TypeName(mapping.Type);
                        writer.Line($"case {typeName} typed:");
                        using (writer.Indent())
                        {
                            writer.Line($"{ApiJson}.Write<{typeName}>(writer, typed, options);");
                            writer.Line("break;");
                        }
                    }

                    writer.Line($"case {plan.UnrecognizedName} unrecognized:");
                    using (writer.Indent())
                    {
                        writer.Line($"{ApiJson}.Write<{plan.UnrecognizedName}>(writer, unrecognized, options);");
                        writer.Line("break;");
                    }

                    writer.Line("default:");
                    using (writer.Indent())
                    {
                        writer.Line($"throw new {Json}.JsonException(\"Cannot write \" + value.GetType().FullName + \" as {rootName}: it is not a known {rootName} type.\");");
                    }
                }
            }
        }
    }

    private void EmitUnionBase(CodeWriter writer, ApiUnionType union)
    {
        var plan = _plan.Polymorphic[union];
        var name = _plan.TypeName(union);
        WriteTypeDocs(writer, union);
        writer.Line($"[{Serialization}.JsonConverter(typeof({plan.ConverterName}))]");
        using (writer.Block($"{_plan.Accessibility} abstract partial class {name}"))
        {
        }

        writer.Line();
        EmitUnrecognized(writer, union, plan);
        writer.Line();
        EmitPolymorphicConverter(writer, union, plan);
    }

    // ------------------------------------------------------------------ union wrappers

    private void EmitUnionWrapper(CodeWriter writer, ApiUnionType union, UnionWrapperPlan plan)
    {
        var name = _plan.TypeName(union);
        var ambiguous = union.Dispatch == ApiUnionDispatch.Ambiguous;
        WriteTypeDocs(writer, union, ambiguous
            ? "The variants cannot be told apart from the JSON alone: values read from JSON keep the raw JSON, and TryGet methods read it as a variant on request."
            : $"Holds exactly one of its {plan.Variants.Count} variants; use the Is, As and TryGet members or the From factories.");
        writer.Line($"[{Serialization}.JsonConverter(typeof({plan.ConverterName}))]");
        using (writer.Block($"{_plan.Accessibility} sealed partial class {name}"))
        {
            writer.Line("private readonly object? _value;");
            writer.Line($"private readonly {Json}.JsonElement _json;");
            writer.Line();
            using (writer.Block($"internal {name}(object? value, int variant, {Json}.JsonElement json)"))
            {
                writer.Line("_value = value;");
                writer.Line("Variant = variant;");
                writer.Line("_json = json;");
            }

            writer.Line();
            writer.Doc("summary", "The 1-based index of the variant held, in declaration order; 0 when the value was read from JSON that matches no known variant (or, for ambiguous unions, any JSON).");
            writer.Line("public int Variant { get; }");
            writer.Line();
            writer.Doc("summary", "The variant value, or null when only raw JSON is held.");
            writer.Line("public object? Value => _value;");
            writer.Line();
            writer.Doc("summary", "The raw JSON the value was read from, when it was read from JSON and not matched to a variant.");
            writer.Line($"public {Json}.JsonElement? Json => _json.ValueKind == {Json}.JsonValueKind.Undefined ? null : _json;");

            var conversions = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < plan.Variants.Count; i++)
            {
                var (variant, variantName) = plan.Variants[i];
                var index = i + 1;
                var type = _plan.CSharpType(variant.Type, TypeUse.Model, nullable: false);
                writer.Line();
                writer.Doc("summary", $"Creates a {name} holding a {variantName}.");
                writer.Line($"public static {name} From{variantName}({type} value) => new(value, {index}, default);");
                if (CanConvertImplicitly(type) && conversions.Add(type))
                {
                    writer.Line();
                    writer.Doc("summary", $"Converts a {variantName} to a {name}.");
                    writer.Line($"public static implicit operator {name}({type} value) => From{variantName}(value);");
                }

                writer.Line();
                writer.Doc("summary", $"Whether the value is a {variantName}.");
                writer.Line($"public bool Is{variantName} => Variant == {index};");
                writer.Line();
                writer.Doc("summary", $"Returns the {variantName}, or throws when the value is another variant.");
                writer.Line($"public {type} As{variantName}() => Variant == {index} ? ({type})_value! : throw new global::System.InvalidOperationException({Literal.String($"The {name} value is not a {variantName}.")});");
                writer.Line();
                if (ambiguous)
                {
                    writer.Doc("summary", $"Gets the value as a {variantName}: the held variant, or the raw JSON read as a {variantName}.");
                    using (writer.Block($"public bool TryGet{variantName}({Json}.JsonSerializerOptions options, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {type} value)"))
                    {
                        using (writer.Block($"if (Variant == {index})"))
                        {
                            writer.Line($"value = ({type})_value!;");
                            writer.Line("return true;");
                        }

                        using (writer.Block($"if (_json.ValueKind != {Json}.JsonValueKind.Undefined)"))
                        {
                            using (writer.Block("try"))
                            {
                                writer.Line($"value = {ApiJson}.Deserialize<{type}>(_json, options)!;");
                                writer.Line("return value is not null;");
                            }

                            using (writer.Block($"catch ({Json}.JsonException)"))
                            {
                            }
                        }

                        writer.Line("value = default!;");
                        writer.Line("return false;");
                    }
                }
                else
                {
                    writer.Doc("summary", $"Gets the value when it is a {variantName}.");
                    using (writer.Block($"public bool TryGet{variantName}([global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {type} value)"))
                    {
                        writer.Line($"value = Variant == {index} ? ({type})_value! : default!;");
                        writer.Line($"return Variant == {index};");
                    }
                }
            }

            writer.Line();
            writer.Doc("summary", "Returns the variant value or the raw JSON as text.");
            writer.Line("public override string ToString() => _value?.ToString() ?? _json.ToString();");
        }

        writer.Line();
        EmitWrapperConverter(writer, union, plan, name);
    }

    private void EmitWrapperConverter(CodeWriter writer, ApiUnionType union, UnionWrapperPlan plan, string name)
    {
        writer.Doc("summary", $"Reads and writes {name} values.");
        using (writer.Block($"{_plan.Accessibility} sealed class {plan.ConverterName} : {Serialization}.JsonConverter<{name}>"))
        {
            writer.Doc("summary", "Reads the variant the JSON represents.");
            using (writer.Block($"public override {name}? Read(ref {Json}.Utf8JsonReader reader, global::System.Type typeToConvert, {Json}.JsonSerializerOptions options)"))
            {
                using (writer.Block($"if (reader.TokenType == {Json}.JsonTokenType.Null)"))
                {
                    writer.Line("return null;");
                }

                writer.Line();
                switch (union.Dispatch)
                {
                    case ApiUnionDispatch.Discriminator:
                        using (writer.Block($"switch ({ApiJson}.PeekDiscriminator(reader, {Literal.String(union.DiscriminatorPropertyName!)}))"))
                        {
                            for (var i = 0; i < plan.Variants.Count; i++)
                            {
                                var (variant, _) = plan.Variants[i];
                                var type = _plan.CSharpType(variant.Type, nullable: false);
                                writer.Line($"case {Literal.String(variant.DiscriminatorValue!)}:");
                                using (writer.Indent())
                                {
                                    writer.Line($"return new {name}({ApiJson}.Read<{type}>(ref reader, options), {i + 1}, default);");
                                }
                            }
                        }

                        break;
                    case ApiUnionDispatch.ValueKind:
                        using (writer.Block("switch (reader.TokenType)"))
                        {
                            for (var i = 0; i < plan.Variants.Count; i++)
                            {
                                var (variant, _) = plan.Variants[i];
                                var type = _plan.CSharpType(variant.Type, nullable: false);
                                foreach (var token in TokenTypes(variant.Type))
                                {
                                    writer.Line($"case {Json}.JsonTokenType.{token}:");
                                }

                                using (writer.Indent())
                                {
                                    writer.Line($"return new {name}({ApiJson}.Read<{type}>(ref reader, options), {i + 1}, default);");
                                }
                            }
                        }

                        break;
                }

                writer.Line();
                writer.Line($"return new {name}(null, 0, {Json}.JsonElement.ParseValue(ref reader));");
            }

            writer.Line();
            writer.Doc("summary", "Writes the held variant, or the raw JSON.");
            using (writer.Block($"public override void Write({Json}.Utf8JsonWriter writer, {name} value, {Json}.JsonSerializerOptions options)"))
            {
                using (writer.Block("switch (value.Variant)"))
                {
                    for (var i = 0; i < plan.Variants.Count; i++)
                    {
                        var (variant, _) = plan.Variants[i];
                        var type = _plan.CSharpType(variant.Type, nullable: false);
                        writer.Line($"case {i + 1}:");
                        using (writer.Indent())
                        {
                            writer.Line($"{ApiJson}.Write<{type}>(writer, ({type})value.Value!, options);");
                            writer.Line("return;");
                        }
                    }

                    writer.Line("default:");
                    using (writer.Indent())
                    {
                        using (writer.Block("if (value.Json is { } json)"))
                        {
                            writer.Line("json.WriteTo(writer);");
                        }
                        using (writer.Block("else"))
                        {
                            writer.Line("writer.WriteNullValue();");
                        }

                        writer.Line("return;");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> TokenTypes(ApiTypeRef type)
    {
        switch (ApiModelBuilderAccess.JsonKind(type))
        {
            case "number":
                yield return "Number";
                break;
            case "boolean":
                yield return "True";
                yield return "False";
                break;
            case "string":
                yield return "String";
                break;
            case "array":
                yield return "StartArray";
                break;
            case "object":
                yield return "StartObject";
                break;
        }
    }

    private static bool CanConvertImplicitly(string type) =>
        !type.StartsWith("global::System.Collections.Generic.IReadOnly", StringComparison.Ordinal) &&
        !type.StartsWith("global::System.Collections.Generic.IEnumerable", StringComparison.Ordinal) &&
        type != "object";

    // ------------------------------------------------------------------ enums

    private void EmitEnum(CodeWriter writer, ApiEnumType type)
    {
        var name = _plan.TypeName(type);
        var converter = name + "JsonConverter";
        var isString = type.UnderlyingKind == ApiPrimitiveKind.String;
        var members = _plan.EnumMembers(type);
        var underlying = isString ? null : type.Members.All(m => m.Value is ScalarNode s && s.TryGetInt64(out var v) && v is >= int.MinValue and <= int.MaxValue) ? null : "long";

        WriteTypeDocs(writer, type);
        writer.Line($"[{Serialization}.JsonConverter(typeof({converter}))]");
        using (writer.Block($"{_plan.Accessibility} enum {name}{(underlying is null ? string.Empty : " : " + underlying)}"))
        {
            foreach (var (member, memberName) in members)
            {
                if (_plan.Options.GenerateDocumentation)
                {
                    writer.Doc("summary", member.Description ?? (isString ? member.ValueText : $"The value {member.ValueText}."));
                }

                writer.Line(isString ? memberName + "," : $"{memberName} = {IntegerValue(member)},");
            }
        }

        writer.Line();
        writer.Doc("summary", $"Converts {name} values to and from their JSON representation. Unknown values fail with a JsonException that names the value.");
        using (writer.Block($"{_plan.Accessibility} sealed class {converter} : {Serialization}.JsonConverter<{name}>"))
        {
            writer.Doc("summary", "Returns the wire value of an enum member.");
            using (writer.Block($"public static string ToWireValue({name} value) => value switch", "};"))
            {
                foreach (var (member, memberName) in members)
                {
                    writer.Line($"{name}.{memberName} => {Literal.String(isString ? member.ValueText : IntegerValue(member))},");
                }

                writer.Line("_ => throw new global::System.ArgumentOutOfRangeException(nameof(value), value, null),");
            }

            writer.Line();
            writer.Doc("summary", "Parses a wire value.");
            using (writer.Block($"public static bool TryParse(string? value, out {name} result)"))
            {
                using (writer.Block("switch (value)"))
                {
                    foreach (var (member, memberName) in members)
                    {
                        writer.Line($"case {Literal.String(isString ? member.ValueText : IntegerValue(member))}:");
                        using (writer.Indent())
                        {
                            writer.Line($"result = {name}.{memberName};");
                            writer.Line("return true;");
                        }
                    }

                    writer.Line("default:");
                    using (writer.Indent())
                    {
                        writer.Line("result = default;");
                        writer.Line("return false;");
                    }
                }
            }

            writer.Line();
            writer.Doc("summary", "Reads a value.");
            using (writer.Block($"public override {name} Read(ref {Json}.Utf8JsonReader reader, global::System.Type typeToConvert, {Json}.JsonSerializerOptions options)"))
            {
                if (isString)
                {
                    writer.Line($"var text = reader.TokenType == {Json}.JsonTokenType.String ? reader.GetString() : throw new {Json}.JsonException({Literal.String($"A {name} value must be a JSON string.")});");
                }
                else
                {
                    writer.Line($"var text = reader.TokenType == {Json}.JsonTokenType.Number ? reader.GetInt64().ToString(global::System.Globalization.CultureInfo.InvariantCulture) : throw new {Json}.JsonException({Literal.String($"A {name} value must be a JSON number.")});");
                }

                writer.Line($"return TryParse(text, out var result) ? result : throw new {Json}.JsonException(\"'\" + text + \"' is not a valid {name} value.\");");
            }

            writer.Line();
            writer.Doc("summary", "Writes a value.");
            using (writer.Block($"public override void Write({Json}.Utf8JsonWriter writer, {name} value, {Json}.JsonSerializerOptions options)"))
            {
                writer.Line(isString
                    ? "writer.WriteStringValue(ToWireValue(value));"
                    : "writer.WriteNumberValue((long)value);");
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An integer enum value as a C# literal (1e2 in the contract becomes 100).</summary>
    private static string IntegerValue(ApiEnumMember member) =>
        member.Value is ScalarNode scalar && scalar.TryGetInt64(out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : "0";

    private void WriteTypeDocs(CodeWriter writer, ApiNamedType type, string? remarks = null)
    {
        if (_plan.Options.GenerateDocumentation)
        {
            writer.Doc("summary", type.Description ?? type.Title ?? (type.Origin == ApiTypeOrigin.Component ? $"The {type.NameHint[0]} schema." : $"Generated from {type.Pointer}."));
            writer.Doc("remarks", remarks);
        }

        if (type.Deprecated)
        {
            writer.Line("[global::System.Obsolete]");
        }
    }

    private static void WriteMembers(CodeWriter writer, List<Action> members)
    {
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                writer.Line();
            }

            members[i]();
        }
    }

    private string Describe(ApiTypeRef type) => _plan.CSharpType(type).Replace("global::System.Collections.Generic.", string.Empty).Replace("global::System.", string.Empty);

    private string DiscriminatorLiteral(ApiProperty discriminator, string value)
    {
        if (discriminator.Type is ApiNamedTypeRef { Target: ApiEnumType enumType } && _plan.IsEmitted(enumType))
        {
            return $"{_plan.NamedType(enumType)}JsonConverter.TryParse({Literal.String(value)}, out var parsed) ? parsed : default";
        }

        return Literal.String(value);
    }

    private static string ConstantLiteral(DocNode constant, string type) => constant switch
    {
        ScalarNode { Kind: NodeKind.String } s => Literal.String(s.Value),
        ScalarNode { Kind: NodeKind.Boolean } b => b.Value,
        ScalarNode { Kind: NodeKind.Number } n => type.TrimEnd('?') switch
        {
            "decimal" when n.TryGetDecimal(out var m) => m.ToString(CultureInfo.InvariantCulture) + "m",
            "float" when n.TryGetDouble(out var f) => ((float)f).ToString("R", CultureInfo.InvariantCulture) + "f",
            "double" when n.TryGetDouble(out var d) => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            _ when n.TryGetInt64(out var l) => l.ToString(CultureInfo.InvariantCulture) + (type.TrimEnd('?') == "long" ? "L" : string.Empty),
            _ => "default!",
        },
        _ => "default!",
    };
}

/// <summary>Access to API-model helpers shared with code generation.</summary>
internal static class ApiModelBuilderAccess
{
    public static string? JsonKind(ApiTypeRef type) => type switch
    {
        ApiPrimitiveType { Kind: ApiPrimitiveKind.Integer or ApiPrimitiveKind.Number } => "number",
        ApiPrimitiveType { Kind: ApiPrimitiveKind.Boolean } => "boolean",
        ApiPrimitiveType { Kind: ApiPrimitiveKind.Any or ApiPrimitiveKind.Binary } => null,
        ApiPrimitiveType => "string",
        ApiArrayType => "array",
        ApiMapType => "object",
        ApiNamedTypeRef { Target: ApiObjectType } => "object",
        ApiNamedTypeRef { Target: ApiEnumType e } => e.UnderlyingKind == ApiPrimitiveKind.String ? "string" : "number",
        _ => null,
    };
}
