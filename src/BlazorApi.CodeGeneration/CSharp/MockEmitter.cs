using System.Globalization;
using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Model;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>
/// Emits test doubles: a configurable mock per client (on <c>BlazorApi.Testing.ApiMock</c>) and fixture factories
/// that build representative model instances. Generated only on request, for test projects.
/// </summary>
internal sealed class MockEmitter
{
    private const string Runtime = "global::BlazorApi.Runtime";
    private const string Tasks = "global::System.Threading.Tasks";
    private const int MaxDepth = 4;

    private readonly GenerationPlan _plan;
    private readonly ClientEmitter _clients;

    public MockEmitter(GenerationPlan plan, ClientEmitter clients)
    {
        _plan = plan;
        _clients = clients;
    }

    // ------------------------------------------------------------------ mocks

    public GeneratedFile EmitMock(ClientPlan client)
    {
        var methods = _clients.MethodsFor(client);
        var name = _plan.TypeScope.Allocate(client.ClassName + "Mock");
        var members = new NameScope(reserved:
        [
            name, "Calls", "CallsTo", "Verify", "VerifyNoOtherCalls", "Reset", "Setup", "SetupAsync", "SetupError",
            "SetupNetworkFailure", "SetupTimeout", "SetupException", "SetupDelay", "InvokeAsync", "Operations",
        ]);
        foreach (var method in methods)
        {
            members.Reserve(method.Operation.Name + "Async");
            members.Reserve(method.Operation.Name + "ResultAsync");
        }

        var writer = FileWriter.Start(_plan, _plan.Namespace);
        var interfaces = _plan.Options.GenerateInterfaces ? ", " + client.InterfaceName : string.Empty;
        writer.Doc("summary", $"A test double of {(_plan.Options.GenerateInterfaces ? client.InterfaceName : client.ClassName)}: set up results per operation, then verify the recorded calls.");
        writer.Doc("remarks", "Operations without a setup fail with status 501. Setup methods return the mock for chaining.");
        using (writer.Block($"{_plan.Accessibility} partial class {name} : global::BlazorApi.Testing.ApiMock{interfaces}"))
        {
            writer.Doc("summary", "The operation identifiers used by Setup, Verify and CallsTo.");
            using (writer.Block("public static class Operations"))
            {
                var constants = new NameScope();
                foreach (var method in methods)
                {
                    writer.Line($"public const string {constants.Allocate(method.Operation.Name)} = {Literal.String(method.Operation.Id)};");
                }
            }

            foreach (var method in methods)
            {
                writer.Line();
                EmitSetups(writer, method, name, members);
                writer.Line();
                EmitImplementation(writer, method);
            }
        }

        return new GeneratedFile(name + ".g.cs", writer.ToString());
    }

    private void EmitSetups(CodeWriter writer, ClientEmitter.Method method, string mockName, NameScope members)
    {
        var id = Literal.String(method.Operation.Id);
        var setup = members.Allocate("Setup" + method.Operation.Name);
        var status = SuccessStatus(method);
        if (method.ValueType is null)
        {
            writer.Doc("summary", $"Makes {method.Operation.Id} succeed.");
            using (writer.Block($"public {mockName} {setup}()"))
            {
                writer.Line($"Setup({id}, _ => {Runtime}.ApiResults.NoContent({status}));");
                writer.Line("return this;");
            }

            return;
        }

        var value = method.ValueType;
        writer.Doc("summary", $"Makes {method.Operation.Id} return a value.");
        using (writer.Block($"public {mockName} {setup}({value} value)"))
        {
            writer.Line($"Setup<{value}>({id}, _ => {Runtime}.ApiResults.Success(value, {status}));");
            writer.Line("return this;");
        }

        if (method.Parameters.Count is > 0 and <= 15)
        {
            writer.Line();
            writer.Doc("summary", $"Makes {method.Operation.Id} compute its value from the call arguments.");
            var types = string.Join(", ", method.Parameters.Select(p => p.Type).Concat([value]));
            var arguments = string.Join(", ", method.Parameters.Select(p => $"call.Argument<{p.Type}>({Literal.String(p.Name.TrimStart('@'))})"));
            using (writer.Block($"public {mockName} {setup}(global::System.Func<{types}> respond)"))
            {
                writer.Line("global::System.ArgumentNullException.ThrowIfNull(respond);");
                writer.Line($"Setup<{value}>({id}, call => {Runtime}.ApiResults.Success(respond({arguments}), {status}));");
                writer.Line("return this;");
            }
        }
    }

    private static string SuccessStatus(ClientEmitter.Method method)
    {
        var success = method.Operation.Operation.SuccessResponses.FirstOrDefault(r => method.ValueType is null ? r.Contents.Count == 0 : r.Contents.Count > 0);
        return (success?.Status ?? (method.ValueType is null ? 204 : 200)).ToString(CultureInfo.InvariantCulture);
    }

    private void EmitImplementation(CodeWriter writer, ClientEmitter.Method method)
    {
        var name = method.Operation.Name;
        var arguments = method.Parameters.Count == 0
            ? "new global::System.Collections.Generic.Dictionary<string, object?>()"
            : "new global::System.Collections.Generic.Dictionary<string, object?> { " +
              string.Join(", ", method.Parameters.Select(p => $"[{Literal.String(p.Name.TrimStart('@'))}] = {p.Name}")) + " }";
        var obsolete = method.Operation.Operation.Deprecated ? "[global::System.Obsolete(\"This operation is deprecated by the API.\")]" : null;

        writer.Line("/// <inheritdoc />");
        if (obsolete is not null)
        {
            writer.Line(obsolete);
        }

        if (method.ValueType is null)
        {
            using (writer.Block($"public async {Tasks}.Task {name}Async({method.Signature})"))
            {
                writer.Line($"(await {name}ResultAsync({method.Arguments}).ConfigureAwait(false)).EnsureSuccess();");
            }

            writer.Line();
            writer.Line("/// <inheritdoc />");
            if (obsolete is not null)
            {
                writer.Line(obsolete);
            }

            writer.Line($"public {Tasks}.Task<{Runtime}.ApiResult> {name}ResultAsync({method.Signature}) =>");
            using (writer.Indent())
            {
                writer.Line($"InvokeAsync({Literal.String(method.Operation.Id)}, {arguments}, cancellationToken);");
            }

            return;
        }

        using (writer.Block($"public async {Tasks}.Task<{method.ValueType}> {name}Async({method.Signature})"))
        {
            writer.Line($"return (await {name}ResultAsync({method.Arguments}).ConfigureAwait(false)).GetValueOrThrow();");
        }

        writer.Line();
        writer.Line("/// <inheritdoc />");
        if (obsolete is not null)
        {
            writer.Line(obsolete);
        }

        writer.Line($"public {Tasks}.Task<{Runtime}.ApiResult<{method.ValueType}>> {name}ResultAsync({method.Signature}) =>");
        using (writer.Indent())
        {
            writer.Line($"InvokeAsync<{method.ValueType}>({Literal.String(method.Operation.Id)}, {arguments}, cancellationToken);");
        }
    }

    // ------------------------------------------------------------------ fixtures

    public GeneratedFile? EmitFixtures()
    {
        var types = _plan.Types.Where(t => t is not ApiEnumType).ToList();
        if (types.Count == 0)
        {
            return null;
        }

        var baseName = Identifier.Pascal(_plan.Clients.FirstOrDefault()?.ClassName is { } client && client.EndsWith("Client", StringComparison.Ordinal) ? client.Substring(0, client.Length - 6) : "Api");
        var name = _plan.TypeScope.Allocate(baseName + "Fixtures");
        var writer = FileWriter.Start(_plan, _plan.ModelsNamespace);
        writer.Doc("summary", "Factories for representative model instances: required properties are set from examples, defaults or type-appropriate values, and nested models are built a few levels deep.");
        writer.Doc("remarks", "Pass a configure action to adjust an instance. Values are deterministic, so they are safe for snapshot tests.");
        using (writer.Block($"{_plan.Accessibility} static partial class {name}"))
        {
            var first = true;
            foreach (var type in types)
            {
                if (!first)
                {
                    writer.Line();
                }

                first = false;
                EmitFixture(writer, type);
            }
        }

        return new GeneratedFile("Models/" + name + ".g.cs", writer.ToString());
    }

    private void EmitFixture(CodeWriter writer, ApiNamedType type)
    {
        var typeName = _plan.TypeName(type);
        writer.Doc("summary", $"Creates a representative {typeName}.");
        using (writer.Block($"public static {typeName} Create{typeName}(global::System.Action<{typeName}>? configure = null)"))
        {
            writer.Line($"var value = Create{typeName}Core(0);");
            writer.Line("configure?.Invoke(value);");
            writer.Line("return value;");
        }

        writer.Line();
        using (writer.Block($"internal static {typeName} Create{typeName}Core(int depth)"))
        {
            switch (type)
            {
                case ApiObjectType obj when _plan.Polymorphic.TryGetValue(obj, out var polymorphic):
                    writer.Line($"return Create{_plan.TypeName(polymorphic.Mappings[0].Type)}Core(depth);");
                    break;
                case ApiUnionType union when _plan.Polymorphic.TryGetValue(union, out var unionPlan):
                    writer.Line($"return Create{_plan.TypeName(unionPlan.Mappings[0].Type)}Core(depth);");
                    break;
                case ApiUnionType union:
                    var wrapper = _plan.Wrappers[union];
                    var (variant, variantName) = wrapper.Variants[0];
                    writer.Line($"return {typeName}.From{variantName}({Value(variant.Type, "value", null, "depth")});");
                    break;
                case ApiObjectType obj:
                    writer.Line($"return new {typeName}");
                    writer.Line("{");
                    using (writer.Indent())
                    {
                        foreach (var property in obj.AllProperties)
                        {
                            var shape = _plan.PropertyShape(property, OwnerOf(obj, property));
                            if (shape.IsConstant || shape.IsDiscriminator)
                            {
                                continue;
                            }

                            var nested = property.Type is ApiNamedTypeRef { Target: ApiObjectType or ApiUnionType } or ApiArrayType or ApiMapType;
                            var value = Value(property.Type, property.JsonName, property, "depth + 1");
                            if (shape.Required)
                            {
                                writer.Line($"{_plan.PropertyName(obj, property)} = {(nested ? $"depth > {MaxDepth} ? default! : {value}" : value)},");
                            }
                            else if (nested)
                            {
                                writer.Line($"{_plan.PropertyName(obj, property)} = depth < 2 ? {value} : default,");
                            }
                            else
                            {
                                writer.Line($"{_plan.PropertyName(obj, property)} = {value},");
                            }
                        }
                    }

                    writer.Line("};");
                    break;
            }
        }
    }

    private static ApiObjectType OwnerOf(ApiObjectType type, ApiProperty property)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Properties.Contains(property))
            {
                return current;
            }
        }

        return type;
    }

    /// <summary>A C# expression for a representative value of a type.</summary>
    private string Value(ApiTypeRef type, string hint, ApiProperty? property, string depth)
    {
        switch (type)
        {
            case ApiPrimitiveType primitive:
                return Primitive(primitive, hint, property);
            case ApiArrayType array:
                var item = Value(array.Items, hint, null, depth);
                var itemType = _plan.CSharpType(array.Items);
                return _plan.Options.Collections == CollectionStyle.Mutable
                    ? $"new global::System.Collections.Generic.List<{itemType}> {{ {item} }}"
                    : $"new {itemType}[] {{ {item} }}";
            case ApiMapType map:
                return $"new global::System.Collections.Generic.Dictionary<string, {_plan.CSharpType(map.Values)}> {{ [\"key\"] = {Value(map.Values, hint, null, depth)} }}";
            case ApiNamedTypeRef { Target: ApiEnumType enumType } when _plan.IsEmitted(enumType):
                return $"{_plan.NamedType(enumType)}.{_plan.EnumMembers(enumType)[0].Name}";
            case ApiNamedTypeRef named when _plan.IsEmitted(named.Target):
                return $"Create{_plan.TypeName(named.Target)}Core({depth})";
            default:
                return "global::System.Text.Json.JsonDocument.Parse(\"{}\").RootElement.Clone()";
        }
    }

    private string Primitive(ApiPrimitiveType type, string hint, ApiProperty? property)
    {
        var csharp = _plan.Primitive(type);
        var example = (type.ConstValue ?? property?.Example ?? property?.Default) as ScalarNode;
        switch (csharp)
        {
            case "string":
                if (example is { Kind: NodeKind.String })
                {
                    return Literal.String(example.Value);
                }

                return Literal.String(type.Kind switch
                {
                    ApiPrimitiveKind.Date => "2024-01-01",
                    ApiPrimitiveKind.Time => "12:00:00Z",
                    ApiPrimitiveKind.Duration => "PT1H",
                    _ => type.Format switch
                    {
                        "email" => "user@example.com",
                        "hostname" => "example.com",
                        "ipv4" => "192.0.2.1",
                        "ipv6" => "2001:db8::1",
                        "password" => "P@ssw0rd!",
                        _ => hint,
                    },
                });
            case "bool":
                return example is { Kind: NodeKind.Boolean } ? example.Value : "true";
            case "int" or "long" or "short" or "sbyte" or "byte" or "ushort" or "uint" or "ulong":
                if (example is { Kind: NodeKind.Number } && example.TryGetInt64(out var integer))
                {
                    return $"({csharp}){integer.ToString(CultureInfo.InvariantCulture)}";
                }

                var minimum = property?.Constraints.Minimum ?? (property?.Constraints.ExclusiveMinimum + 1) ?? 1;
                return $"({csharp}){decimal.Ceiling(minimum).ToString(CultureInfo.InvariantCulture)}";
            case "double" or "float" or "decimal":
                var number = example is { Kind: NodeKind.Number } && example.TryGetDecimal(out var parsed) ? parsed : property?.Constraints.Minimum ?? 1.5m;
                var suffix = csharp switch { "decimal" => "m", "float" => "f", _ => "d" };
                return number.ToString(CultureInfo.InvariantCulture) + suffix;
            case "global::System.DateOnly":
                return "new global::System.DateOnly(2024, 1, 1)";
            case "global::System.TimeOnly":
                return "new global::System.TimeOnly(12, 0)";
            case "global::System.DateTimeOffset":
                return "new global::System.DateTimeOffset(2024, 1, 1, 12, 0, 0, global::System.TimeSpan.Zero)";
            case "global::System.DateTime":
                return "new global::System.DateTime(2024, 1, 1, 12, 0, 0, global::System.DateTimeKind.Utc)";
            case "global::System.Guid":
                return "new global::System.Guid(\"00000000-0000-0000-0000-000000000001\")";
            case "global::System.Uri":
                return example is { Kind: NodeKind.String } && Uri.TryCreate(example.Value, UriKind.RelativeOrAbsolute, out _)
                    ? $"new global::System.Uri({Literal.String(example.Value)}, global::System.UriKind.RelativeOrAbsolute)"
                    : "new global::System.Uri(\"https://example.com/\")";
            case "byte[]":
                return "new byte[] { 1, 2, 3 }";
            case "global::BlazorApi.Runtime.FileContent":
                return "global::BlazorApi.Runtime.FileContent.FromBytes(new byte[] { 1, 2, 3 }, \"file.bin\")";
            default:
                return "global::System.Text.Json.JsonDocument.Parse(\"{}\").RootElement.Clone()";
        }
    }
}
