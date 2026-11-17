using System.Globalization;
using System.Net;
using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>
/// Emits a typed view of the success bodies of operations that answer with different body types per status (a 200
/// resource and a 202 job status): a <c>{Operation}ResponseBodies</c> class with one nullable property per status,
/// created from the operation's <c>ApiResult</c>. The result itself keeps one value type (the first success body); the
/// others are in <c>ApiResult.Content</c>, which this class reads by status.
/// </summary>
internal sealed class ResponseBodiesEmitter
{
    private const string Runtime = "global::BlazorApi.Runtime";

    private readonly GenerationPlan _plan;
    private readonly ClientEmitter _clients;

    public ResponseBodiesEmitter(GenerationPlan plan, ClientEmitter clients)
    {
        _plan = plan;
        _clients = clients;
    }

    public GeneratedFile? Emit(ClientPlan client)
    {
        var operations = _clients.MethodsFor(client)
            .Select(m => (Method: m, Bodies: Bodies(m.Operation.Operation)))
            .Where(o => o.Bodies.Select(b => b.Type).Distinct(StringComparer.Ordinal).Count() > 1)
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

            EmitClass(writer, operations[i].Method, operations[i].Bodies);
        }

        return new GeneratedFile(client.ClassName + "ResponseBodies.g.cs", writer.ToString());
    }

    /// <summary>The success responses with a JSON or text body, with the C# type each is read as.</summary>
    private List<(ApiResponse Response, string Type)> Bodies(ApiOperation operation)
    {
        var result = new List<(ApiResponse, string)>();
        foreach (var response in operation.SuccessResponses)
        {
            // The same media type the client reads (ClientEmitter ranks JSON first).
            var content = response.Contents.OrderBy(ClientEmitter.ContentRank).FirstOrDefault();
            if (content is null || content.Kind is not (ApiBodyKind.Json or ApiBodyKind.Text or ApiBodyKind.Other))
            {
                continue;
            }

            var type = content.Kind == ApiBodyKind.Json
                ? _plan.CSharpType(content.Type ?? new ApiPrimitiveType(ApiPrimitiveKind.Any), nullable: false)
                : "string";
            result.Add((response, type));
        }

        return result;
    }

    private void EmitClass(CodeWriter writer, ClientEmitter.Method method, List<(ApiResponse Response, string Type)> bodies)
    {
        var name = _plan.TypeScope.Allocate(method.Operation.Name + "ResponseBodies");
        writer.Doc("summary", $"The success bodies {method.Operation.Id} declares, typed by status: the one that matches the response is set.");
        using (writer.Block($"{_plan.Accessibility} sealed partial class {name}"))
        {
            var members = new NameScope(reserved: [name, "From", "StatusCode"]);
            var properties = bodies.Select(b => (b.Response, b.Type, Property: members.Allocate(PropertyName(b.Response)))).ToList();

            writer.Doc("summary", "Reads the body of a result by its status.");
            using (writer.Block($"public {name}({Runtime}.ApiResult result)"))
            {
                writer.Line("global::System.ArgumentNullException.ThrowIfNull(result);");
                writer.Line("StatusCode = result.StatusCode;");
                // Statuses match like the client: exact codes, then ranges, then default for what is left.
                var exact = method.Operation.Operation.Responses.Where(r => r.Status is not null).Select(r => r.Status!.Value).ToList();
                var ranges = method.Operation.Operation.Responses.Where(r => r.Status is null && r.StatusClass is not null).Select(r => r.StatusClass!.Value).ToList();
                string NotIn(IEnumerable<string> tests) => string.Concat(tests.Select(t => $"{t} && "));
                for (var i = 0; i < properties.Count; i++)
                {
                    var (response, type, property) = properties[i];
                    var notExact = exact.Select(code => $"result.StatusCode != {code.ToString(CultureInfo.InvariantCulture)}");
                    var condition = response.Status is { } status
                        ? $"result.StatusCode == {status.ToString(CultureInfo.InvariantCulture)} && "
                        : response.StatusClass is { } statusClass
                            ? $"result.StatusCode / 100 == {statusClass.ToString(CultureInfo.InvariantCulture)} && " + NotIn(notExact)
                            : NotIn(notExact.Concat(ranges.Select(c => $"result.StatusCode / 100 != {c.ToString(CultureInfo.InvariantCulture)}")));
                    writer.Line($"{property} = result.IsSuccess && {condition}result.Content is {type} body{i} ? body{i} : null;");
                }
            }

            writer.Line();
            writer.Doc("summary", "Reads the body of a result by its status.");
            writer.Line($"public static {name} From({Runtime}.ApiResult result) => new(result);");
            writer.Line();
            writer.Doc("summary", "The response status code (0 when no response arrived).");
            writer.Line("public int StatusCode { get; }");
            foreach (var (response, type, property) in properties)
            {
                writer.Line();
                writer.Doc("summary", string.IsNullOrEmpty(response.Description) ? $"The {response.StatusCode} body." : response.Description);
                writer.Doc("remarks", $"Set when the response status is {response.StatusCode}.");
                writer.Line($"public {type}? {property} {{ get; }}");
            }
        }
    }

    /// <summary>The status name (<c>Ok</c>, <c>Created</c>, <c>Accepted</c>), <c>Success2XX</c> or <c>Default</c>.</summary>
    private static string PropertyName(ApiResponse response)
    {
        if (response.Status is { } status)
        {
            var known = Enum.GetName(typeof(HttpStatusCode), status);
            return known is null ? "Status" + status.ToString(CultureInfo.InvariantCulture) : Identifier.Pascal(known);
        }

        return response.IsDefault ? "Default" : "Success" + response.StatusCode.ToUpperInvariant();
    }
}
