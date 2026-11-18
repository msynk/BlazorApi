using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>
/// Emits a System.Text.Json <c>JsonSerializerContext</c> (<see cref="GeneratorOptions.GenerateJsonContext"/>) that
/// lists every type the generated code serializes: the generic arguments of the runtime JSON helpers in the generated
/// files, the generated models, and the types of parameters, parts and properties (which reach
/// <c>ApiJson.Serialize</c> through type inference). Listing a superset is harmless; a missing type would fail at run
/// time in applications without reflection.
/// </summary>
internal sealed class JsonContextEmitter
{
    private static readonly string[] GenericCalls =
    [
        "ApiJson.Read<", "ApiJson.Write<", "ApiJson.Deserialize<", "ApiJson.Serialize<", "ApiJson.TypeInfo<",
        "ApiBody.ReadJson<", "ApiBody.JsonError<", "ApiBody.ReadEventStream<", "ApiBody.ReadJsonSequence<",
        "ApiBody.Json<", ".AddJson<", "OptionalJsonConverter<",
    ];

    private static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "char",
        "global::System.Guid", "global::System.DateTimeOffset", "global::System.DateTime", "global::System.DateOnly",
        "global::System.TimeOnly", "global::System.TimeSpan", "global::System.Text.Json.JsonElement",
    };

    private readonly GenerationPlan _plan;

    public JsonContextEmitter(GenerationPlan plan) => _plan = plan;

    public GeneratedFile Emit(IReadOnlyList<GeneratedFile> files)
    {
        var enumNames = new HashSet<string>(StringComparer.Ordinal);
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in _plan.Types.Where(_plan.IsEmitted))
        {
            var typeName = _plan.TypeName(type);
            if (type is ApiEnumType)
            {
                enumNames.Add(typeName);
                enumNames.Add("global::" + _plan.ModelsNamespace + "." + typeName);
            }

            types.Add(typeName);
        }

        void AddType(string csharp)
        {
            var normalized = Normalize(csharp, enumNames);
            // Only the runtime's non-JSON types are left out (by their full names, so user types like FileResponseDto stay).
            if (normalized.Length > 0 &&
                !normalized.Contains("global::BlazorApi.Runtime.Http.ApiNoContent") &&
                !normalized.Contains("global::BlazorApi.Runtime.FileContent") &&
                !normalized.Contains("global::BlazorApi.Runtime.FileResponse"))
            {
                types.Add(normalized);
            }
        }

        foreach (var file in files)
        {
            foreach (var argument in GenericArguments(file.Content))
            {
                AddType(argument);
            }
        }

        foreach (var client in _plan.Clients)
        {
            foreach (var operation in client.Operations.Select(o => o.Operation))
            {
                foreach (var parameter in operation.Parameters)
                {
                    AddType(_plan.CSharpType(parameter.Type, TypeUse.Parameter, nullable: false));
                }

                foreach (var content in operation.RequestBody?.Contents ?? [])
                {
                    if (content.Type is { } bodyType)
                    {
                        AddType(_plan.CSharpType(bodyType, TypeUse.Model, nullable: false));
                    }

                    foreach (var part in content.Parts)
                    {
                        AddType(_plan.CSharpType(part.Type, TypeUse.Parameter, nullable: false));
                        AddType(_plan.CSharpType(part.Type, TypeUse.Model, nullable: false));
                    }
                }
            }
        }

        foreach (var obj in _plan.Types.OfType<ApiObjectType>().Where(_plan.IsEmitted))
        {
            foreach (var property in _plan.DeclaredProperties(obj))
            {
                AddType(_plan.CSharpType(property.Type, TypeUse.Model, nullable: false));
            }
        }

        return EmitContext(types, enumNames);
    }

    // ------------------------------------------------------------------ metadata

    private const string Meta = "global::System.Text.Json.Serialization.Metadata";
    private const string Ser = "global::System.Text.Json.Serialization";

    /// <summary>The built-in System.Text.Json converters of the primitive types the generator uses.</summary>
    private static readonly Dictionary<string, string> Primitives = new(StringComparer.Ordinal)
    {
        ["string"] = "StringConverter", ["bool"] = "BooleanConverter", ["byte"] = "ByteConverter", ["sbyte"] = "SByteConverter",
        ["short"] = "Int16Converter", ["ushort"] = "UInt16Converter", ["int"] = "Int32Converter", ["uint"] = "UInt32Converter",
        ["long"] = "Int64Converter", ["ulong"] = "UInt64Converter", ["float"] = "SingleConverter", ["double"] = "DoubleConverter",
        ["decimal"] = "DecimalConverter", ["char"] = "CharConverter", ["object"] = "ObjectConverter", ["byte[]"] = "ByteArrayConverter",
        ["global::System.Guid"] = "GuidConverter", ["global::System.DateTime"] = "DateTimeConverter",
        ["global::System.DateTimeOffset"] = "DateTimeOffsetConverter", ["global::System.DateOnly"] = "DateOnlyConverter",
        ["global::System.TimeOnly"] = "TimeOnlyConverter", ["global::System.TimeSpan"] = "TimeSpanConverter",
        ["global::System.Uri"] = "UriConverter", ["global::System.Text.Json.JsonElement"] = "JsonElementConverter",
    };

    /// <summary>One property of an object's metadata.</summary>
    private sealed record MetaProperty(string Name, string? JsonName, string Type, string DeclaringType, string? IgnoreCondition, string? Converter, bool Required, bool IsExtension);

    /// <summary>
    /// Emits the context as a <c>JsonSerializerContext</c> written with the public <c>JsonMetadataServices</c> API (what
    /// the System.Text.Json source generator emits), so it works without that generator: in the BlazorApi source
    /// generator, whose output other generators cannot see, as well as in CLI output. Types the generator does not
    /// know (mapped types) get no metadata; their resolver must come after this context.
    /// </summary>
    private GeneratedFile EmitContext(SortedSet<string> roots, ISet<string> enumNames)
    {
        var name = ContextName();
        var lookup = NamedTypes();
        var methods = new Dictionary<string, string>(StringComparer.Ordinal);
        var bodies = new List<Action<CodeWriter>>();
        var queue = new Queue<string>(roots);

        var mapped = MappedTypePattern();
        var modelPrefix = new System.Text.RegularExpressions.Regex(
            "global::" + System.Text.RegularExpressions.Regex.Escape(_plan.ModelsNamespace) + @"\.(?=[A-Za-z_@][A-Za-z0-9_]*(?![\w.]))");

        string? MethodFor(string rawType)
        {
            // Generated types are named without their namespace (the context lives in it), so each has one entry.
            var type = modelPrefix.Replace(Normalize(rawType, enumNames), string.Empty);
            if (methods.TryGetValue(type, out var known))
            {
                return known;
            }

            if (mapped?.IsMatch(type) == true)
            {
                return null; // A type with a mapped part: the user's resolver provides its metadata.
            }

            var body = Shape(type, lookup, enumNames, queue);
            if (body is null)
            {
                return null;
            }

            var method = "Create" + methods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            methods[type] = method;
            bodies.Add(writer => EmitCreate(writer, method, type, body));
            return method;
        }

        while (queue.Count > 0)
        {
            MethodFor(queue.Dequeue());
        }

        var file = FileWriter.Start(_plan, _plan.ModelsNamespace);
        file.Line("#nullable disable warnings");
        file.Line();
        file.Doc("summary", $"System.Text.Json metadata for the types of the {_plan.Model.Title} API, written at generation time so trimmed, native AOT and Blazor WebAssembly applications need no reflection.");
        file.Doc("remarks", "Generated clients and their registration methods use it by default. To build options yourself, pass it to ApiJson.CreateOptions: ApiJson.CreateOptions(" + name + ".Default).");
        using (file.Block($"{_plan.Accessibility} partial class {name} : {Ser}.JsonSerializerContext, {Meta}.IJsonTypeInfoResolver"))
        {
            file.Line("private static readonly global::System.Text.Json.JsonSerializerOptions DefaultOptions = new();");
            file.Line();
            file.Doc("summary", "The context with default options.");
            file.Line($"public static {name} Default {{ get; }} = new(new global::System.Text.Json.JsonSerializerOptions(DefaultOptions));");
            file.Line();
            file.Doc("summary", "Creates the context with default options.");
            file.Line($"public {name}()");
            file.Line("    : base(null)");
            file.Line("{");
            file.Line("}");
            file.Line();
            file.Doc("summary", "Creates the context for options.");
            file.Line($"public {name}(global::System.Text.Json.JsonSerializerOptions options)");
            file.Line("    : base(options)");
            file.Line("{");
            file.Line("}");
            file.Line();
            file.Line("/// <inheritdoc />");
            file.Line("protected override global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions { get; } = DefaultOptions;");
            file.Line();
            file.Line("/// <inheritdoc />");
            using (file.Block($"public override {Meta}.JsonTypeInfo? GetTypeInfo(global::System.Type type)"))
            {
                file.Line($"Options.TryGetTypeInfo(type, out {Meta}.JsonTypeInfo? typeInfo);");
                file.Line("return typeInfo;");
            }

            file.Line();
            file.Line("/// <inheritdoc />");
            using (file.Block($"{Meta}.JsonTypeInfo? {Meta}.IJsonTypeInfoResolver.GetTypeInfo(global::System.Type type, global::System.Text.Json.JsonSerializerOptions options)"))
            {
                foreach (var entry in methods.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    using (file.Block($"if (type == typeof({entry.Key}))"))
                    {
                        file.Line($"return {entry.Value}(options);");
                    }
                }

                file.Line("return null;");
            }

            foreach (var body in bodies)
            {
                file.Line();
                body(file);
            }

            file.Line();
            using (file.Block($"private static bool TryGetRuntimeConverter<T>(global::System.Text.Json.JsonSerializerOptions options, out {Meta}.JsonTypeInfo<T> typeInfo)"))
            {
                file.Line("// A converter registered in the options (for example ApiJson's duration converter) wins, as in generated contexts.");
                using (file.Block("foreach (var converter in options.Converters)"))
                {
                    using (file.Block("if (converter?.CanConvert(typeof(T)) == true)"))
                    {
                        file.Line($"typeInfo = {Meta}.JsonMetadataServices.CreateValueInfo<T>(options, Expand(typeof(T), converter, options));");
                        file.Line("return true;");
                    }
                }

                file.Line("typeInfo = null!;");
                file.Line("return false;");
            }

            file.Line();
            using (file.Block($"private static {Ser}.JsonConverter Expand(global::System.Type type, {Ser}.JsonConverter converter, global::System.Text.Json.JsonSerializerOptions options)"))
            {
                file.Line($"return converter is {Ser}.JsonConverterFactory factory");
                file.Line("    ? factory.CreateConverter(type, options) ?? throw new global::System.InvalidOperationException(\"The converter factory \" + factory.GetType() + \" returned null.\")");
                file.Line("    : converter;");
            }
        }

        return new GeneratedFile(name + ".g.cs", file.ToString());
    }

    private void EmitCreate(CodeWriter writer, string method, string type, Action<CodeWriter> body)
    {
        using (writer.Block($"private {Meta}.JsonTypeInfo<{type}> {method}(global::System.Text.Json.JsonSerializerOptions options)"))
        {
            using (writer.Block($"if (!TryGetRuntimeConverter<{type}>(options, out var typeInfo))"))
            {
                body(writer);
            }

            writer.Line("typeInfo.OriginatingResolver = this;");
            writer.Line("return typeInfo;");
        }
    }

    /// <summary>What a generated type name stands for.</summary>
    private sealed class NamedInfo
    {
        public string? Converter { get; init; }

        public string? Creator { get; init; }

        public IReadOnlyList<MetaProperty> Properties { get; init; } = [];
    }

    /// <summary>The generated types by name: converters for enums, unions and hierarchies, properties for classes.</summary>
    private Dictionary<string, NamedInfo> NamedTypes()
    {
        var result = new Dictionary<string, NamedInfo>(StringComparer.Ordinal);
        foreach (var type in _plan.Types.Where(_plan.IsEmitted))
        {
            var name = _plan.TypeName(type);
            switch (type)
            {
                case ApiEnumType:
                    result[name] = new NamedInfo { Converter = name + "JsonConverter" };
                    break;
                case ApiUnionType union:
                    result[name] = new NamedInfo { Converter = _plan.Polymorphic.TryGetValue(union, out var unionPlan) ? unionPlan.ConverterName : _plan.Wrappers[union].ConverterName };
                    break;
                case ApiObjectType obj when _plan.Polymorphic.TryGetValue(obj, out var rootPlan):
                    result[name] = new NamedInfo { Converter = rootPlan.ConverterName };
                    break;
                case ApiObjectType obj when _plan.Subtrees.TryGetValue(obj, out var subtree):
                    result[name] = new NamedInfo { Converter = subtree.Converter };
                    result[subtree.Payload] = ObjectInfo(obj, subtree.Payload, declaredHere: true, extraExtension: null);
                    break;
                case ApiObjectType obj:
                    result[name] = ObjectInfo(obj, name, declaredHere: false, extraExtension: null);
                    break;
            }
        }

        foreach (var (root, plan) in _plan.Polymorphic.Select(p => (p.Key, p.Value)))
        {
            // Unrecognized{Root}: the root's properties plus an extension data property (inherited or its own).
            string? own = null;
            if (root is not ApiObjectType rootObject || _plan.ExtensionDataOwner(rootObject) is null)
            {
                own = root is ApiObjectType o && o.AllProperties.Any(p => _plan.PropertyName(o, p) == "AdditionalProperties") ? "UnrecognizedProperties" : "AdditionalProperties";
            }

            result[plan.UnrecognizedName] = root is ApiObjectType robj
                ? ObjectInfo(robj, plan.UnrecognizedName, declaredHere: false, extraExtension: own)
                : new NamedInfo { Creator = $"new {plan.UnrecognizedName}()", Properties = [Extension(own!, plan.UnrecognizedName)] };
        }

        return result;
    }

    /// <summary>
    /// A pattern that matches the C# types configured in <see cref="GeneratorOptions.TypeMappings"/> (other than
    /// built-in types), or <see langword="null"/>.
    /// </summary>
    private System.Text.RegularExpressions.Regex? MappedTypePattern()
    {
        var mapped = _plan.Options.TypeMappings.Values
            .Select(GenerationPlan.Qualify)
            .Where(t => !Primitives.ContainsKey(t) && !ValueTypes.Contains(t) && t is not ("string" or "object"))
            .Distinct(StringComparer.Ordinal)
            .Select(System.Text.RegularExpressions.Regex.Escape)
            .ToList();
        return mapped.Count == 0
            ? null
            : new System.Text.RegularExpressions.Regex(@"(?<![\w.:])(?:" + string.Join("|", mapped) + @")(?![\w.])");
    }

    /// <summary>
    /// The metadata of a class with the properties of <paramref name="obj"/>: as the class itself (properties declared
    /// along its chain) or as a type that redeclares them all (<paramref name="declaredHere"/>, payload classes).
    /// </summary>
    private NamedInfo ObjectInfo(ApiObjectType obj, string typeName, bool declaredHere, string? extraExtension)
    {
        // The order reflection uses: a class's own properties first, then each base's (a payload class declares all).
        var ordered = declaredHere
            ? obj.AllProperties.ToList()
            : _plan.ClassChain(obj).SelectMany(_plan.DeclaredProperties).ToList();
        var properties = new List<MetaProperty>();
        foreach (var property in ordered)
        {
            var contract = _plan.Contract(obj, property);
            var declaring = declaredHere ? typeName : _plan.TypeName(_plan.DeclaringType(obj, property));
            properties.Add(new MetaProperty(contract.Name, property.JsonName, contract.Type, declaring, contract.IgnoreCondition, contract.Converter, contract.Required, IsExtension: false));
        }

        if (_plan.ExtensionDataOwner(obj) is { } owner)
        {
            properties.Add(Extension(_plan.ExtensionDataName(owner), declaredHere ? typeName : _plan.TypeName(owner)));
        }
        else if (extraExtension is not null)
        {
            properties.Add(Extension(extraExtension, typeName));
        }

        var required = properties.Where(p => p.Required).Select(p => $"{p.Name} = default!");
        return new NamedInfo { Creator = $"new {typeName} {{ {string.Join(", ", required)} }}", Properties = properties };
    }

    private static MetaProperty Extension(string name, string declaring) =>
        new(name, null, "global::System.Collections.Generic.IDictionary<string, global::System.Text.Json.JsonElement>", declaring, null, null, Required: false, IsExtension: true);

    /// <summary>The body that creates the metadata of a type, or <see langword="null"/> for types without metadata.</summary>
    private Action<CodeWriter>? Shape(string type, Dictionary<string, NamedInfo> lookup, ISet<string> enumNames, Queue<string> queue)
    {
        if (Primitives.TryGetValue(type, out var primitive))
        {
            return w => w.Line($"typeInfo = {Meta}.JsonMetadataServices.CreateValueInfo<{type}>(options, {Meta}.JsonMetadataServices.{primitive});");
        }

        if (type.EndsWith("?", StringComparison.Ordinal))
        {
            var underlying = type.Substring(0, type.Length - 1);
            queue.Enqueue(underlying);
            return w => w.Line($"typeInfo = {Meta}.JsonMetadataServices.CreateValueInfo<{type}>(options, {Meta}.JsonMetadataServices.GetNullableConverter<{underlying}>(options));");
        }

        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            var element = type.Substring(0, type.Length - 2);
            queue.Enqueue(element);
            return w => Collection(w, type, null, $"CreateArrayInfo<{element}>");
        }

        var lt = type.IndexOf('<');
        if (lt > 0 && type.EndsWith(">", StringComparison.Ordinal))
        {
            var generic = type.Substring(0, lt);
            var arguments = SplitArguments(type.Substring(lt + 1, type.Length - lt - 2));
            foreach (var argument in arguments)
            {
                queue.Enqueue(argument);
            }

            var simple = generic.Substring(generic.LastIndexOf('.') + 1);
            return simple switch
            {
                "Optional" when arguments.Count == 1 => w => w.Line($"typeInfo = {Meta}.JsonMetadataServices.CreateValueInfo<{type}>(options, Expand(typeof({type}), new global::BlazorApi.Runtime.Json.OptionalJsonConverter<{arguments[0]}>(), options));"),
                "IReadOnlyList" or "IEnumerable" or "IReadOnlyCollection" when arguments.Count == 1 => w => Collection(w, type, null, $"CreateIEnumerableInfo<{type}, {arguments[0]}>"),
                "List" when arguments.Count == 1 => w => Collection(w, type, $"new {type}()", $"CreateListInfo<{type}, {arguments[0]}>"),
                "IReadOnlyDictionary" when arguments.Count == 2 => w => Collection(w, type, $"new global::System.Collections.Generic.Dictionary<{arguments[0]}, {arguments[1]}>()", $"CreateIReadOnlyDictionaryInfo<{type}, {arguments[0]}, {arguments[1]}>"),
                "Dictionary" when arguments.Count == 2 => w => Collection(w, type, $"new {type}()", $"CreateDictionaryInfo<{type}, {arguments[0]}, {arguments[1]}>"),
                "IDictionary" when arguments.Count == 2 => w => Collection(w, type, $"new global::System.Collections.Generic.Dictionary<{arguments[0]}, {arguments[1]}>()", $"CreateIDictionaryInfo<{type}, {arguments[0]}, {arguments[1]}>"),
                _ => null,
            };
        }

        var prefix = "global::" + _plan.ModelsNamespace + ".";
        var key = type.StartsWith(prefix, StringComparison.Ordinal) ? type.Substring(prefix.Length) : type;
        if (!lookup.TryGetValue(key, out var info))
        {
            return null; // A mapped or unknown type: another resolver must provide its metadata.
        }

        if (info.Converter is { } converter)
        {
            return w => w.Line($"typeInfo = {Meta}.JsonMetadataServices.CreateValueInfo<{type}>(options, Expand(typeof({type}), new {converter}(), options));");
        }

        foreach (var property in info.Properties)
        {
            queue.Enqueue(property.Type);
        }

        return w => ObjectBody(w, type, info, enumNames);
    }

    private static void Collection(CodeWriter writer, string type, string? creator, string factory)
    {
        writer.Line($"var info = new {Meta}.JsonCollectionInfoValues<{type}>");
        writer.Line("{");
        writer.Line($"    ObjectCreator = {(creator is null ? "null" : "() => " + creator)},");
        writer.Line("};");
        writer.Line($"typeInfo = {Meta}.JsonMetadataServices.{factory}(options, info);");
        writer.Line("typeInfo.NumberHandling = null; // Use the options' number handling, as reflection does.");
    }

    private static void ObjectBody(CodeWriter writer, string type, NamedInfo info, ISet<string> enumNames)
    {
        writer.Line($"var objectInfo = new {Meta}.JsonObjectInfoValues<{type}>");
        writer.Line("{");
        writer.Line($"    ObjectCreator = static () => {info.Creator},");
        writer.Line("    PropertyMetadataInitializer = _ =>");
        writer.Line("    {");
        writer.Line($"        var properties = new {Meta}.JsonPropertyInfo[{info.Properties.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}];");
        for (var i = 0; i < info.Properties.Count; i++)
        {
            var p = info.Properties[i];
            // The declared type, annotations included (a nullable mapped struct is Nullable<T>); typeof needs it normalized.
            var propertyType = p.Type;
            var index = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            writer.Line($"        properties[{index}] = {Meta}.JsonMetadataServices.CreatePropertyInfo<{propertyType}>(options, new {Meta}.JsonPropertyInfoValues<{propertyType}>");
            writer.Line("        {");
            writer.Line("            IsProperty = true,");
            writer.Line("            IsPublic = true,");
            writer.Line($"            DeclaringType = typeof({p.DeclaringType}),");
            writer.Line($"            Converter = {(p.Converter is null ? "null" : $"({Ser}.JsonConverter<{propertyType}>)Expand(typeof({Normalize(propertyType, enumNames)}), new {p.Converter}(), options)")},");
            writer.Line($"            Getter = static obj => (({p.DeclaringType})obj).{p.Name},");
            writer.Line($"            Setter = static (obj, value) => (({p.DeclaringType})obj).{p.Name} = value!,");
            writer.Line($"            IgnoreCondition = {(p.IgnoreCondition is null ? "null" : $"{Ser}.JsonIgnoreCondition.{p.IgnoreCondition}")},");
            writer.Line($"            IsExtensionData = {(p.IsExtension ? "true" : "false")},");
            writer.Line($"            PropertyName = {Literal.String(p.Name)},");
            writer.Line($"            JsonPropertyName = {(p.JsonName is null ? "null" : Literal.String(p.JsonName))},");
            writer.Line("        });");
            if (p.Required)
            {
                writer.Line($"        properties[{index}].IsRequired = true;");
            }

            if (!p.IsExtension && !p.Type.EndsWith("?", StringComparison.Ordinal) && !ValueTypes.Contains(p.Type) && !enumNames.Contains(p.Type) &&
                !p.Type.StartsWith("global::BlazorApi.Runtime.Json.Optional<", StringComparison.Ordinal))
            {
                // Non-nullable declarations, enforced like reflection does with RespectNullableAnnotations (.NET 9+).
                writer.Line("#if NET9_0_OR_GREATER");
                writer.Line($"        properties[{index}].IsGetNullable = false;");
                writer.Line($"        properties[{index}].IsSetNullable = false;");
                writer.Line("#endif");
            }
        }

        writer.Line("        return properties;");
        writer.Line("    },");
        writer.Line("};");
        writer.Line($"typeInfo = {Meta}.JsonMetadataServices.CreateObjectInfo<{type}>(options, objectInfo);");
        writer.Line("typeInfo.NumberHandling = null; // Use the options' number handling, as reflection does.");
    }

    /// <summary>Splits generic arguments at top-level commas.</summary>
    private static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch { '<' => 1, '>' => -1, _ => 0 };
            if (text[i] == ',' && depth == 0)
            {
                result.Add(text.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        result.Add(text.Substring(start).Trim());
        return result;
    }

    /// <summary>The context class name, qualified for use from the client namespace.</summary>
    public string QualifiedName => _plan.ModelsNamespace == _plan.Namespace ? ContextName() : "global::" + _plan.ModelsNamespace + "." + ContextName();

    private string? _contextName;

    private string ContextName()
    {
        if (_contextName is null)
        {
            var client = _plan.Clients.FirstOrDefault(c => c.Tag is null) ?? _plan.Clients.FirstOrDefault();
            var stem = client?.ClassName ?? Identifier.Pascal(_plan.Model.Title);
            if (stem.EndsWith("Client", StringComparison.Ordinal) && stem.Length > "Client".Length)
            {
                stem = stem.Substring(0, stem.Length - "Client".Length);
            }

            _contextName = _plan.TypeScope.Allocate(stem + "JsonContext");
        }

        return _contextName;
    }

    /// <summary>The generic arguments of the runtime JSON helper calls in a source text.</summary>
    private static IEnumerable<string> GenericArguments(string text)
    {
        foreach (var call in GenericCalls)
        {
            var index = 0;
            while ((index = text.IndexOf(call, index, StringComparison.Ordinal)) >= 0)
            {
                var start = index + call.Length;
                var depth = 1;
                var i = start;
                while (i < text.Length && depth > 0)
                {
                    depth += text[i] switch { '<' => 1, '>' => -1, _ => 0 };
                    i++;
                }

                index = start;
                if (depth == 0)
                {
                    yield return text.Substring(start, i - 1 - start);
                }
            }
        }
    }

    /// <summary>
    /// Makes a type usable in <c>typeof</c>: nullable annotations of reference types are removed (they are not
    /// allowed there and do not change the metadata), nullable value types are kept.
    /// </summary>
    internal static string Normalize(string type, ISet<string> enumNames)
    {
        var builder = new System.Text.StringBuilder(type.Length);
        var identifier = new System.Text.StringBuilder();
        foreach (var c in type.Trim())
        {
            if (c == '?')
            {
                var previous = identifier.ToString();
                if (ValueTypes.Contains(previous) || enumNames.Contains(previous))
                {
                    builder.Append('?');
                }

                identifier.Clear();
                continue;
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '@')
            {
                identifier.Append(c);
            }
            else
            {
                identifier.Clear();
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
