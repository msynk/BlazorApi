using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Model;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>
/// Emits dotnet-query integration: stable query keys, query definitions for GET operations, mutations with
/// invalidation metadata, and infinite queries for paginated operations. Caching stays in dotnet-query.
/// </summary>
internal sealed class QueryEmitter
{
    private const string Query = "global::DotNetQuery";
    private const string Defaults = "global::BlazorApi.DotNetQuery.ApiQueryDefaults";

    private readonly GenerationPlan _plan;
    private readonly ClientEmitter _clients;
    private readonly PaginationEmitter _pagination;

    public QueryEmitter(GenerationPlan plan, ClientEmitter clients, PaginationEmitter pagination)
    {
        _plan = plan;
        _clients = clients;
        _pagination = pagination;
    }

    public GeneratedFile Emit(ClientPlan client)
    {
        var methods = _clients.MethodsFor(client);
        var queries = methods.Where(IsQuery).ToList();
        var mutations = methods.Where(m => m.Operation.Operation.Method != "GET" && m.Operation.Operation.Method != "HEAD").ToList();
        var target = _plan.Options.GenerateInterfaces ? client.InterfaceName : client.ClassName;
        var baseName = client.ClassName.EndsWith("Client", StringComparison.Ordinal) && client.ClassName.Length > 6
            ? client.ClassName.Substring(0, client.ClassName.Length - 6)
            : client.ClassName;
        var className = _plan.TypeScope.Allocate(baseName + "Queries");
        var members = new NameScope(reserved: [className, "All", "Keys"]);
        var keyMembers = new NameScope(reserved: ["Keys"]);
        var names = new Dictionary<ClientEmitter.Method, (string Query, string Prefix, string Key)>();
        foreach (var method in queries)
        {
            names[method] = (members.Allocate(method.Operation.Name), keyMembers.Allocate(method.Operation.Name + "Prefix"), keyMembers.Allocate(method.Operation.Name));
        }

        var writer = FileWriter.Start(_plan, _plan.Namespace);
        writer.Doc("summary", $"dotnet-query keys, query definitions and mutations for {target}.");
        writer.Doc("remarks", "Every key starts with All, so invalidating All refreshes every query of this client. Mutations invalidate the queries of related resources after they settle.");
        using (writer.Block($"{_plan.Accessibility} static partial class {className}"))
        {
            writer.Doc("summary", "The root key of every query of this client.");
            writer.Line($"public static readonly {Query}.QueryKey All = {Query}.QueryKey.Create({Literal.String(client.ClassName)});");
            writer.Line();
            writer.Doc("summary", "Query keys: a prefix per operation (matches every argument combination) and a key per argument combination.");
            using (writer.Block("public static class Keys"))
            {
                for (var i = 0; i < queries.Count; i++)
                {
                    var method = queries[i];
                    var (_, prefix, key) = names[method];
                    if (i > 0)
                    {
                        writer.Line();
                    }

                    writer.Doc("summary", $"The prefix of every {method.Operation.Id} query key.");
                    writer.Line($"public static readonly {Query}.QueryKey {prefix} = All.Append({Literal.String(method.Operation.Id)});");
                    writer.Line();
                    writer.Doc("summary", $"The key of a {method.Operation.Id} query.");
                    var parameters = method.ParameterList();
                    var segments = string.Join(", ", method.Parameters.Select(KeySegment));
                    writer.Line(method.Parameters.Count == 0
                        ? $"public static {Query}.QueryKey {key}() => {prefix};"
                        : $"public static {Query}.QueryKey {key}({parameters}) => {prefix}.Append({segments});");
                }
            }

            foreach (var method in queries)
            {
                writer.Line();
                EmitQuery(writer, method, names[method], target);
                if (_pagination.Analyze(method) is { } walk)
                {
                    writer.Line();
                    EmitInfinite(writer, walk, names[method], target, members);
                }
            }

            foreach (var method in mutations)
            {
                writer.Line();
                EmitMutation(writer, method, methods, names, target, members);
            }
        }

        return new GeneratedFile(className + ".g.cs", writer.ToString());
    }

    private static bool IsQuery(ClientEmitter.Method method) =>
        method.Operation.Operation.Method == "GET" && !method.IsFile && method.ValueType is not null;

    private static string KeySegment(ClientEmitter.Parameter parameter) =>
        parameter.Type.StartsWith("global::System.Collections.Generic.IEnumerable", StringComparison.Ordinal)
            ? $"{parameter.Name} is null ? null : global::System.Linq.Enumerable.ToArray({parameter.Name})"
            : parameter.Name;

    private void EmitQuery(CodeWriter writer, ClientEmitter.Method method, (string Query, string Prefix, string Key) names, string target)
    {
        var operation = method.Operation.Operation;
        var scope = new NameScope(reserved: method.Parameters.Select(p => p.Name.TrimStart('@')));
        var client = scope.Allocate("client");
        var options = scope.Allocate("options");
        var parameters = method.ParameterList();
        writer.Doc("summary", $"A query definition for {method.Operation.Id}: {operation.Summary ?? operation.Method + " " + operation.Path}");
        writer.Doc("remarks", "Pass it to the dotnet-query client or a Blazor query component. Without options, transient failures are retried and client errors are not.");
        WriteObsolete(writer, method);
        writer.Line($"public static {Query}.QueryDefinition<{method.ValueType}> {names.Query}({target} {client}{(parameters.Length > 0 ? ", " + parameters : string.Empty)}, {Query}.QueryOptions? {options} = null)");
        using (writer.Indent())
        {
            var arguments = string.Join(", ", method.Parameters.Select(p => p.Name));
            var call = string.Join(", ", method.Parameters.Select(p => p.Name).Concat(["cancellationToken"]));
            writer.Line($"=> new({SequenceKey(names.Key, arguments)}, cancellationToken => {client}.{method.Operation.Name}Async({call}), {Defaults}.WithDefaults({options}));");
        }
    }

    private static string SequenceKey(string key, string arguments) => $"Keys.{key}({arguments})";

    private void EmitInfinite(CodeWriter writer, PaginationEmitter.Walk walk, (string Query, string Prefix, string Key) names, string target, NameScope members)
    {
        var method = walk.Method;
        var name = members.Allocate(method.Operation.Name + "Infinite");
        var scope = new NameScope(reserved: method.Parameters.Select(p => p.Name.TrimStart('@')));
        var queryClient = scope.Allocate("queryClient");
        var client = scope.Allocate("client");
        var options = scope.Allocate("options");
        var others = method.Parameters.Where(p => !ReferenceEquals(p, walk.Parameter)).ToList();
        var parameters = method.ParameterList(p => !ReferenceEquals(p, walk.Parameter));
        var paramType = walk.Pagination.Style is ApiPaginationStyle.Cursor or ApiPaginationStyle.LinkHeader
            ? walk.ParameterType + "?"
            : walk.ParameterType;
        var pageType = method.ValueType!;
        var call = string.Join(", ", method.Parameters.Select(p => ReferenceEquals(p, walk.Parameter) ? "pageParam" : p.Name).Concat(["cancellationToken"]));
        var keySegments = string.Join(", ", new[] { Literal.String("infinite") }.Concat(others.Select(KeySegment)));

        writer.Doc("summary", $"An infinite query over the pages of {method.Operation.Id}.");
        writer.Doc("remarks", "Fetch more pages with FetchNextPageAsync; the query stops when the API reports no more pages.");
        WriteObsolete(writer, method);
        using (writer.Block($"public static {Query}.InfiniteQuery<{paramType}, {pageType}> {name}({Query}.IQueryClient {queryClient}, {target} {client}{(parameters.Length > 0 ? ", " + parameters : string.Empty)}, {Query}.QueryOptions? {options} = null)"))
        {
            writer.Line($"global::System.ArgumentNullException.ThrowIfNull({queryClient});");
            writer.Line($"global::System.ArgumentNullException.ThrowIfNull({client});");
            if (walk.Pagination.Style == ApiPaginationStyle.LinkHeader)
            {
                // The next page comes from the response headers, which the value-returning method does not expose.
                writer.Line($"var nextPages = new global::System.Runtime.CompilerServices.ConditionalWeakTable<object, string>();");
            }

            writer.Line($"return {queryClient}.CreateInfiniteQuery<{paramType}, {pageType}>(");
            using (writer.Indent())
            {
                writer.Line($"Keys.{names.Prefix}.Append({keySegments}),");
                if (walk.Pagination.Style == ApiPaginationStyle.LinkHeader)
                {
                    writer.Line("async (pageParam, cancellationToken) =>");
                    writer.Line("{");
                    using (writer.Indent())
                    {
                        var resultCall = call;
                        writer.Line($"var result = await {client}.{method.Operation.Name}ResultAsync({resultCall}).ConfigureAwait(false);");
                        writer.Line("var page = result.GetValueOrThrow();");
                        writer.Line($"if (page is not null && global::BlazorApi.Runtime.Http.ApiLinkHeader.GetQueryParameter(result.Headers, {Literal.String(walk.Parameter.Source!.Name)}) is {{ }} next)");
                        writer.Line("{");
                        using (writer.Indent())
                        {
                            writer.Line("nextPages.AddOrUpdate(page, next);");
                        }

                        writer.Line("}");
                        writer.Line();
                        writer.Line("return page;");
                    }

                    writer.Line("},");
                }
                else
                {
                    writer.Line($"(pageParam, cancellationToken) => {client}.{method.Operation.Name}Async({call}),");
                }

                writer.Line($"new {Query}.InfiniteQueryOptions<{paramType}, {pageType}>");
                writer.Line("{");
                using (writer.Indent())
                {
                    writer.Line($"InitialPageParam = {walk.Start},");
                    writer.Line($"GetNextPageParam = (currentPage_, allPages, allPageParams) => {NextPageExpression(walk, paramType, method)},");
                    writer.Line($"QueryOptions = {Defaults}.WithDefaults({options}),");
                }

                writer.Line("});");
            }
        }
    }

    private string NextPageExpression(PaginationEmitter.Walk walk, string paramType, ClientEmitter.Method method)
    {
        var none = $"{Query}.NextPageParam.None<{paramType}>()";
        switch (walk.Pagination.Style)
        {
            case ApiPaginationStyle.Cursor:
                var hasMore = walk.HasMoreExpression is null ? string.Empty : $" && {walk.HasMoreExpression} != false";
                return $"{walk.NextCursorExpression} is {{ Length: > 0 }} next && next != allPageParams[allPageParams.Count - 1]{hasMore} ? {Query}.NextPageParam.Of<{paramType}>(next) : {none}";
            case ApiPaginationStyle.LinkHeader:
                var parse = walk.ParameterType == "string"
                    ? $"{Query}.NextPageParam.Of<{paramType}>(next)"
                    : $"{walk.ParameterType}.TryParse(next, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? {Query}.NextPageParam.Of<{paramType}>(parsed) : {none}";
                return $"currentPage_ is not null && nextPages.TryGetValue(currentPage_, out var next) ? {parse} : {none}";
            default:
                var conditions = new List<string>();
                var count = walk.ItemsExpression is null ? null : $"global::System.Linq.Enumerable.Count({walk.ItemsExpression})";
                if (count is not null)
                {
                    conditions.Add($"{count} > 0");
                }

                if (walk.HasMoreExpression is not null)
                {
                    conditions.Add($"{walk.HasMoreExpression} != false");
                }

                var current = "allPageParams[allPageParams.Count - 1]";
                if (walk.Pagination.Style == ApiPaginationStyle.Page && walk.TotalPagesExpression is not null)
                {
                    conditions.Add($"!({walk.TotalPagesExpression} is {{ }} totalPages && {current} >= totalPages)");
                }

                if (walk.Pagination.Style == ApiPaginationStyle.Offset && walk.TotalExpression is not null && count is not null)
                {
                    conditions.Add($"!({walk.TotalExpression} is {{ }} total && {current} + {count} >= total)");
                }

                var nextValue = walk.Pagination.Style == ApiPaginationStyle.Page
                    ? $"({walk.ParameterType})({current} + 1)"
                    : $"({walk.ParameterType})({current} + {count})";
                return $"{string.Join(" && ", conditions)} ? {Query}.NextPageParam.Of<{paramType}>({nextValue}) : {none}";
        }
    }

    private void EmitMutation(
        CodeWriter writer,
        ClientEmitter.Method method,
        List<ClientEmitter.Method> all,
        Dictionary<ClientEmitter.Method, (string Query, string Prefix, string Key)> names,
        string target,
        NameScope members)
    {
        var operation = method.Operation.Operation;
        var name = members.Allocate(method.Operation.Name);
        var optionsName = members.Allocate(method.Operation.Name + "Options");
        string variables;
        string invoke;
        if (method.Parameters.Count == 0)
        {
            variables = "object?";
            invoke = $"{method.Operation.Name}Async(cancellationToken)";
        }
        else if (method.Parameters.Count == 1)
        {
            variables = method.Parameters[0].Type;
            invoke = $"{method.Operation.Name}Async(variables, cancellationToken)";
        }
        else
        {
            var record = members.Allocate(method.Operation.Name + "Variables");
            writer.Doc("summary", $"The variables of the {method.Operation.Id} mutation.");
            var recordParameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {Identifier.Pascal(p.Name.TrimStart('@'))}{(p.Optional ? " = null" : string.Empty)}"));
            writer.Line($"public sealed record {record}({recordParameters});");
            writer.Line();
            variables = record;
            invoke = $"{method.Operation.Name}Async({string.Join(", ", method.Parameters.Select(p => "variables." + Identifier.Pascal(p.Name.TrimStart('@'))))}, cancellationToken)";
        }

        var data = method.ValueType ?? "bool";
        var invalidated = Invalidated(method, all).Where(names.ContainsKey).Select(m => "Keys." + names[m].Prefix).ToList();
        var invalidates = invalidated.Count == 0 ? $"global::System.Array.Empty<{Query}.QueryKey>()" : "[" + string.Join(", ", invalidated) + "]";
        var optionsType = $"{Query}.MutationOptions<{variables}, {data}>";

        writer.Doc("summary", $"Mutation options for {method.Operation.Id}: its mutation key and the queries it invalidates.");
        writer.Doc("remarks", invalidated.Count == 0
            ? "No queries are invalidated by default; pass Invalidates to change that."
            : "Invalidates " + string.Join(", ", invalidated) + " unless the given options set Invalidates.");
        WriteObsolete(writer, method);
        writer.Line($"public static {optionsType} {optionsName}({optionsType}? options = null) =>");
        using (writer.Indent())
        {
            writer.Line($"(options ?? new {optionsType}()) with");
            writer.Line("{");
            using (writer.Indent())
            {
                writer.Line($"MutationKey = options?.MutationKey ?? All.Append({Literal.String(method.Operation.Id)}),");
                writer.Line($"Invalidates = options?.Invalidates ?? {invalidates},");
            }

            writer.Line("};");
        }

        writer.Line();
        writer.Doc("summary", $"Creates a mutation for {method.Operation.Id}: {operation.Summary ?? operation.Method + " " + operation.Path}");
        WriteObsolete(writer, method);
        var lambda = method.ValueType is null
            ? $"async (variables, cancellationToken) => {{ await client.{invoke}.ConfigureAwait(false); return true; }}"
            : $"(variables, cancellationToken) => client.{invoke}";
        writer.Line($"public static {Query}.Mutation<{variables}, {data}> {name}({Query}.IQueryClient queryClient, {target} client, {optionsType}? options = null) =>");
        using (writer.Indent())
        {
            writer.Line($"queryClient.CreateMutation<{variables}, {data}>({lambda}, {optionsName}(options));");
        }
    }

    /// <summary>
    /// The queries a mutation invalidates: configured invalidations, then <c>x-invalidates</c>, then queries whose path
    /// is an ancestor, the same as, or a descendant of the mutation's path.
    /// </summary>
    private IEnumerable<ClientEmitter.Method> Invalidated(ClientEmitter.Method mutation, List<ClientEmitter.Method> all)
    {
        var operation = mutation.Operation.Operation;
        var queries = all.Where(IsQuery).ToList();
        IReadOnlyList<string>? explicitIds = null;
        if (operation.OperationId is { } id && _plan.Options.Invalidations.TryGetValue(id, out var configured))
        {
            explicitIds = configured;
        }
        else if (operation.Source.TryGetExtension("x-invalidates", out var extension) && extension is ArrayNode array)
        {
            explicitIds = array.Items.OfType<ScalarNode>().Select(s => s.Text).ToList();
        }

        if (explicitIds is not null)
        {
            return queries.Where(q => q.Operation.Operation.OperationId is { } qid && explicitIds.Contains(qid));
        }

        var path = Segments(operation.Path);
        return queries.Where(q =>
        {
            var other = Segments(q.Operation.Operation.Path);
            var shared = Math.Min(path.Count, other.Count);
            return shared > 0 && path.Take(shared).SequenceEqual(other.Take(shared));
        });
    }

    private static List<string> Segments(string path) =>
        path.Split('/').Where(s => s.Length > 0).Select(s => s.StartsWith("{", StringComparison.Ordinal) ? "{}" : s).ToList();

    private static void WriteObsolete(CodeWriter writer, ClientEmitter.Method method)
    {
        if (method.Operation.Operation.Deprecated)
        {
            writer.Line("[global::System.Obsolete(\"This operation is deprecated by the API.\")]");
        }
    }
}
