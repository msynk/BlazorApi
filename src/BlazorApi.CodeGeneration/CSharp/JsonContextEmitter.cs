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
            }

            types.Add(typeName);
        }

        void AddType(string csharp)
        {
            var normalized = Normalize(csharp, enumNames);
            if (normalized.Length > 0 && !normalized.Contains("ApiNoContent") && !normalized.Contains("FileContent") && !normalized.Contains("FileResponse"))
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
            foreach (var property in obj.Properties)
            {
                AddType(_plan.CSharpType(property.Type, TypeUse.Model, nullable: false));
            }
        }

        var name = ContextName();
        var writer = FileWriter.Start(_plan, _plan.ModelsNamespace);
        writer.Doc("summary", $"System.Text.Json metadata for the types of the {_plan.Model.Title} API, generated at compile time for trimmed, native AOT and Blazor WebAssembly applications.");
        writer.Doc("remarks", "Generated clients and their registration methods use it by default. To build options yourself, pass it to ApiJson.CreateOptions: ApiJson.CreateOptions(" + name + ".Default).");
        foreach (var type in types)
        {
            writer.Line($"[global::System.Text.Json.Serialization.JsonSerializable(typeof({type}))]");
        }

        writer.Line($"{_plan.Accessibility} partial class {name} : global::System.Text.Json.Serialization.JsonSerializerContext");
        writer.Line("{");
        writer.Line("}");
        return new GeneratedFile(name + ".g.cs", writer.ToString());
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
                if (ValueTypes.Contains(previous) || enumNames.Contains(previous) || enumNames.Contains(previous.Substring(previous.LastIndexOf('.') + 1)))
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
