using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>
/// Emits a typed view of the response headers each operation declares (<c>ETag</c>, <c>Location</c>,
/// <c>Retry-After</c>, rate limits): a <c>{Operation}ResponseHeaders</c> class per operation with declared headers,
/// created from an <c>ApiResult</c>, an <c>ApiError</c> or a header dictionary.
/// </summary>
internal sealed class HeadersEmitter
{
    private const string Http = "global::BlazorApi.Runtime.Http";
    private const string Runtime = "global::BlazorApi.Runtime";
    private const string HeaderMap = "global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Collections.Generic.IReadOnlyList<string>>";

    private readonly GenerationPlan _plan;
    private readonly ClientEmitter _clients;

    public HeadersEmitter(GenerationPlan plan, ClientEmitter clients)
    {
        _plan = plan;
        _clients = clients;
    }

    public GeneratedFile? Emit(ClientPlan client)
    {
        var operations = _clients.MethodsFor(client)
            .Select(m => (Method: m, Headers: Headers(m.Operation.Operation)))
            .Where(o => o.Headers.Count > 0)
            .ToList();
        if (operations.Count == 0)
        {
            return null;
        }

        var writer = FileWriter.Start(_plan, _plan.Namespace);
        for (var i = 0; i < operations.Count; i++)
        {
            if (i > 0)
            {
                writer.Line();
            }

            EmitClass(writer, operations[i].Method, operations[i].Headers);
        }

        return new GeneratedFile(client.ClassName + "ResponseHeaders.g.cs", writer.ToString());
    }

    /// <summary>The declared headers of every response, by name (case-insensitive); the first declaration wins.</summary>
    private static List<(ApiHeader Header, IReadOnlyList<string> Statuses)> Headers(ApiOperation operation)
    {
        var result = new List<(ApiHeader Header, List<string> Statuses)>();
        foreach (var response in operation.Responses)
        {
            foreach (var header in response.Headers)
            {
                var index = result.FindIndex(h => string.Equals(h.Header.Name, header.Name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    result.Add((header, [response.StatusCode]));
                }
                else
                {
                    result[index].Statuses.Add(response.StatusCode);
                }
            }
        }

        return result.Select(h => (h.Header, (IReadOnlyList<string>)h.Statuses)).ToList();
    }

    private void EmitClass(CodeWriter writer, ClientEmitter.Method method, List<(ApiHeader Header, IReadOnlyList<string> Statuses)> headers)
    {
        var name = _plan.TypeScope.Allocate(method.Operation.Name + "ResponseHeaders");
        writer.Doc("summary", $"The response headers {method.Operation.Id} declares, read from a result or an error.");
        using (writer.Block($"{_plan.Accessibility} sealed partial class {name}"))
        {
            var members = new NameScope(reserved: [name, "From", "Headers"]);
            var properties = headers.Select(h => (h.Header, h.Statuses, Property: members.Allocate(Identifier.Pascal(h.Header.Name)))).ToList();
            var variables = new NameScope(reserved: ["headers"]);
            var variableNames = new Dictionary<ApiHeader, string>();
            foreach (var p in properties)
            {
                var candidate = "h" + p.Property;
                var suffix = 1;
                while (variables.IsUsed(candidate) || variables.IsUsed(candidate + "Parsed"))
                {
                    candidate = "h" + p.Property + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                variables.Reserve(candidate);
                variables.Reserve(candidate + "Parsed");
                variableNames[p.Header] = candidate;
            }

            writer.Doc("summary", "Reads the headers.");
            using (writer.Block($"public {name}({HeaderMap} headers)"))
            {
                writer.Line("Headers = headers ?? throw new global::System.ArgumentNullException(nameof(headers));");
                foreach (var (header, _, property) in properties)
                {
                    writer.Line($"{property} = {Reader(header, "headers", variableNames[header])};");
                }
            }

            writer.Line();
            writer.Doc("summary", "Reads the headers of a result (success or failure).");
            writer.Line($"public static {name} From({Runtime}.ApiResult result) => new((result ?? throw new global::System.ArgumentNullException(nameof(result))).Headers);");
            writer.Line();
            writer.Doc("summary", "Reads the headers of an error response.");
            writer.Line($"public static {name} From({Runtime}.ApiError error) => new((error ?? throw new global::System.ArgumentNullException(nameof(error))).Headers);");
            writer.Line();
            writer.Doc("summary", "All response headers.");
            writer.Line($"public {HeaderMap} Headers {{ get; }}");
            foreach (var (header, statuses, property) in properties)
            {
                writer.Line();
                var where = $"Declared for {string.Join(", ", statuses)}{(header.Required ? " (always sent)" : string.Empty)}.";
                writer.Doc("summary", header.Description ?? $"The '{header.Name}' header.");
                writer.Doc("remarks", where + " Null when absent or not readable as the declared type.");
                writer.Line($"public {PropertyType(header)} {property} {{ get; }}");
            }
        }
    }

    private string PropertyType(ApiHeader header) => Kind(header.Type) switch
    {
        "list" => "global::System.Collections.Generic.IReadOnlyList<string>?",
        "enum" => _plan.CSharpType(header.Type.WithNullable(false), nullable: false) + "?",
        { } kind => kind + "?",
    };

    private string Reader(ApiHeader header, string headers, string variable)
    {
        var name = Literal.String(header.Name);
        return Kind(header.Type) switch
        {
            "list" => $"{Http}.ApiHeaderValues.List({headers}, {name})",
            "enum" => $"{Http}.ApiHeaderValues.String({headers}, {name}) is {{ }} {variable} && {_plan.CSharpType(header.Type.WithNullable(false), nullable: false)}JsonConverter.TryParse({variable}, out var {variable}Parsed) ? {variable}Parsed : null",
            "string" => $"{Http}.ApiHeaderValues.String({headers}, {name})",
            "long" => $"{Http}.ApiHeaderValues.Int64({headers}, {name})",
            "int" => $"{Http}.ApiHeaderValues.Int32({headers}, {name})",
            "double" => $"{Http}.ApiHeaderValues.Double({headers}, {name})",
            "decimal" => $"{Http}.ApiHeaderValues.Decimal({headers}, {name})",
            "bool" => $"{Http}.ApiHeaderValues.Boolean({headers}, {name})",
            "global::System.DateTimeOffset" => $"{Http}.ApiHeaderValues.DateTimeOffset({headers}, {name})",
            "global::System.DateOnly" => $"{Http}.ApiHeaderValues.DateOnly({headers}, {name})",
            "global::System.Guid" => $"{Http}.ApiHeaderValues.Guid({headers}, {name})",
            "global::System.Uri" => $"{Http}.ApiHeaderValues.Uri({headers}, {name})",
            "global::System.TimeSpan" => $"{Http}.ApiHeaderValues.Duration({headers}, {name})",
            _ => $"{Http}.ApiHeaderValues.String({headers}, {name})",
        };
    }

    /// <summary>The supported C# type of a header, or <c>string</c> for anything without a header reader.</summary>
    private string Kind(ApiTypeRef type)
    {
        type = type.WithNullable(false);
        switch (type)
        {
            case ApiArrayType:
                return "list";
            case ApiNamedTypeRef { Target: ApiEnumType e } when _plan.IsEmitted(e) && e.UnderlyingKind == ApiPrimitiveKind.String:
                return "enum";
            case ApiPrimitiveType primitive:
                var csharp = _plan.Primitive(primitive);
                return csharp switch
                {
                    "long" or "int" or "double" or "decimal" or "bool" or "global::System.DateTimeOffset" or "global::System.DateOnly"
                        or "global::System.Guid" or "global::System.Uri" or "global::System.TimeSpan" => csharp,
                    "float" => "double",
                    "global::System.DateTime" => "global::System.DateTimeOffset",
                    _ => "string",
                };
            default:
                return "string";
        }
    }
}
