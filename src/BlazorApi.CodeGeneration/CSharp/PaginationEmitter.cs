using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>
/// Emits <c>IAsyncEnumerable</c> helpers that walk every page (and item) of paginated operations. They need no
/// query library and work against the client interface, so they also work with mocks.
/// </summary>
internal sealed class PaginationEmitter
{
    private const string Generic = "global::System.Collections.Generic";

    private readonly GenerationPlan _plan;
    private readonly ClientEmitter _clients;

    public PaginationEmitter(GenerationPlan plan, ClientEmitter clients)
    {
        _plan = plan;
        _clients = clients;
    }

    /// <summary>Describes how to walk the pages of one method; null when the method cannot be paged safely.</summary>
    internal sealed class Walk
    {
        public Walk(ClientEmitter.Method method, ApiPagination pagination, ClientEmitter.Parameter parameter)
        {
            Method = method;
            Pagination = pagination;
            Parameter = parameter;
        }

        public ClientEmitter.Method Method { get; }

        public ApiPagination Pagination { get; }

        /// <summary>The paging parameter (omitted from the helper signatures).</summary>
        public ClientEmitter.Parameter Parameter { get; }

        public string ParameterType { get; set; } = string.Empty;

        public string? ItemType { get; set; }

        public string? ItemsExpression { get; set; }

        public string? NextCursorExpression { get; set; }

        public string? HasMoreExpression { get; set; }

        public string? TotalExpression { get; set; }

        public string? TotalPagesExpression { get; set; }

        public string Start { get; set; } = "null";
    }

    public Walk? Analyze(ClientEmitter.Method method)
    {
        if (method.Operation.Operation.Pagination is not { } pagination || method.ValueType is null || method.IsFile)
        {
            return null;
        }

        var parameter = method.Parameters.FirstOrDefault(p => ReferenceEquals(p.Source, pagination.Parameter));
        if (parameter is null || !parameter.Optional)
        {
            return null;
        }

        var walk = new Walk(method, pagination, parameter)
        {
            ParameterType = _plan.CSharpType(pagination.Parameter.Type, TypeUse.Parameter, nullable: false),
        };

        if (pagination.ItemsPath is { } itemsPath && pagination.ItemType is { } itemType)
        {
            walk.ItemType = _plan.CSharpType(itemType);
            var (expression, nullable) = Access(pagination.PageType, itemsPath, "currentPage_");
            walk.ItemsExpression = nullable ? $"({expression} ?? global::System.Array.Empty<{walk.ItemType}>())" : expression;
        }

        walk.NextCursorExpression = Path(pagination, pagination.NextCursorPath);
        walk.HasMoreExpression = Path(pagination, pagination.HasMorePath);
        walk.TotalExpression = Path(pagination, pagination.TotalCountPath);
        walk.TotalPagesExpression = Path(pagination, pagination.TotalPagesPath);

        switch (pagination.Style)
        {
            case ApiPaginationStyle.Cursor:
                if (walk.NextCursorExpression is null)
                {
                    return null;
                }

                break;
            case ApiPaginationStyle.Page:
                if (walk.ItemsExpression is null && walk.HasMoreExpression is null && walk.TotalPagesExpression is null)
                {
                    return null;
                }

                walk.Start = pagination.Parameter.Default is OpenApi.Syntax.ScalarNode { Kind: OpenApi.Syntax.NodeKind.Number } start ? start.Value : "1";
                break;
            case ApiPaginationStyle.Offset:
                if (walk.ItemsExpression is null)
                {
                    return null;
                }

                walk.Start = "0";
                break;
        }

        return walk;
    }

    private string? Path(ApiPagination pagination, IReadOnlyList<string>? path) =>
        path is null ? null : Access(pagination.PageType, path, "currentPage_").Expression;

    /// <summary>Builds a null-safe member access along a JSON property path.</summary>
    private (string Expression, bool Nullable) Access(ApiTypeRef root, IReadOnlyList<string> path, string variable)
    {
        var expression = variable;
        var nullable = false;
        var current = root;
        foreach (var segment in path)
        {
            if (current is not ApiNamedTypeRef { Target: ApiObjectType obj } || obj.AllProperties.FirstOrDefault(p => p.JsonName == segment) is not { } property)
            {
                break;
            }

            expression += (nullable ? "?." : ".") + _plan.PropertyName(obj, property);
            nullable |= _plan.PropertyShape(property, obj).CSharpNullable;
            current = property.Type;
        }

        return (expression, nullable);
    }

    public GeneratedFile? Emit(ClientPlan client)
    {
        var walks = _clients.MethodsFor(client).Select(Analyze).Where(w => w is not null).Cast<Walk>().ToList();
        if (walks.Count == 0)
        {
            return null;
        }

        var target = _plan.Options.GenerateInterfaces ? client.InterfaceName : client.ClassName;
        var name = _plan.TypeScope.Allocate(client.ClassName + "Pagination");
        var writer = FileWriter.Start(_plan, _plan.Namespace);
        writer.Doc("summary", $"Walks the pages and items of the paginated operations of {target}.");
        using (writer.Block($"{_plan.Accessibility} static partial class {name}"))
        {
            for (var i = 0; i < walks.Count; i++)
            {
                if (i > 0)
                {
                    writer.Line();
                }

                EmitWalk(writer, walks[i], target);
            }
        }

        return new GeneratedFile(name + ".g.cs", writer.ToString());
    }

    private void EmitWalk(CodeWriter writer, Walk walk, string target)
    {
        var method = walk.Method;
        var name = method.Operation.Name;
        var scope = new NameScope(reserved: method.Parameters.Select(p => p.Name.TrimStart('@')).Concat(["cancellationToken", "currentPage_", "result", "item", "count"]));
        var clientName = scope.Allocate("client");
        var others = method.ParameterList(p => !ReferenceEquals(p, walk.Parameter));
        var signature = $"this {target} {clientName}{(others.Length > 0 ? ", " + others : string.Empty)}, [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken cancellationToken = default";
        var arguments = string.Join(", ", method.Parameters.Select(p => p.Name).Concat(["cancellationToken"]));
        var pageType = method.ValueType!;
        var styleText = walk.Pagination.Style switch
        {
            ApiPaginationStyle.Cursor => $"following the cursor in each page ('{walk.Parameter.Source!.Name}')",
            ApiPaginationStyle.Page => $"incrementing the '{walk.Parameter.Source!.Name}' page number",
            ApiPaginationStyle.Offset => $"advancing the '{walk.Parameter.Source!.Name}' offset",
            _ => $"following the Link header's rel=\"next\" '{walk.Parameter.Source!.Name}' value",
        };

        writer.Doc("summary", $"Enumerates every page of {method.Operation.Id}, {styleText}, until the API reports no more pages.");
        WriteObsolete(writer, method);
        using (writer.Block($"public static async {Generic}.IAsyncEnumerable<{pageType}> {name}PagesAsync({signature})"))
        {
            writer.Line($"global::System.ArgumentNullException.ThrowIfNull({clientName});");
            var parameter = walk.Parameter.Name;
            switch (walk.Pagination.Style)
            {
                case ApiPaginationStyle.Cursor:
                    writer.Line($"{walk.ParameterType}? {parameter} = null;");
                    using (writer.Block("while (true)"))
                    {
                        writer.Line($"var currentPage_ = await {clientName}.{name}Async({arguments}).ConfigureAwait(false);");
                        writer.Line("yield return currentPage_;");
                        writer.Line($"var next_ = {walk.NextCursorExpression};");
                        using (writer.Block($"if (string.IsNullOrEmpty(next_) || next_ == {parameter}{HasMoreStop(walk)})"))
                        {
                            writer.Line("yield break;");
                        }

                        writer.Line($"{parameter} = next_;");
                    }

                    break;
                case ApiPaginationStyle.LinkHeader:
                    writer.Line($"{walk.ParameterType}? {parameter} = null;");
                    using (writer.Block("while (true)"))
                    {
                        writer.Line($"var result_ = await {clientName}.{name}ResultAsync({arguments}).ConfigureAwait(false);");
                        writer.Line("yield return result_.GetValueOrThrow();");
                        writer.Line($"var next_ = global::BlazorApi.Runtime.Http.ApiLinkHeader.GetQueryParameter(result_.Headers, {Literal.String(walk.Parameter.Source!.Name)});");
                        if (walk.ParameterType == "string")
                        {
                            using (writer.Block($"if (string.IsNullOrEmpty(next_) || next_ == {parameter})"))
                            {
                                writer.Line("yield break;");
                            }

                            writer.Line($"{parameter} = next_;");
                        }
                        else
                        {
                            using (writer.Block($"if (!{walk.ParameterType}.TryParse(next_, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var nextValue_) || nextValue_ == {parameter})"))
                            {
                                writer.Line("yield break;");
                            }

                            writer.Line($"{parameter} = nextValue_;");
                        }
                    }

                    break;
                default:
                    var isPage = walk.Pagination.Style == ApiPaginationStyle.Page;
                    writer.Line($"{walk.ParameterType} {parameter}_ = {walk.Start};");
                    using (writer.Block("while (true)"))
                    {
                        var callArguments = string.Join(", ", method.Parameters.Select(p => ReferenceEquals(p, walk.Parameter) ? p.Name + "_" : p.Name).Concat(["cancellationToken"]));
                        writer.Line($"var currentPage_ = await {clientName}.{name}Async({callArguments}).ConfigureAwait(false);");
                        writer.Line("yield return currentPage_;");
                        if (walk.ItemsExpression is not null)
                        {
                            writer.Line($"var count_ = global::System.Linq.Enumerable.Count({walk.ItemsExpression});");
                            using (writer.Block("if (count_ == 0)"))
                            {
                                writer.Line("yield break;");
                            }
                        }

                        if (walk.HasMoreExpression is not null)
                        {
                            using (writer.Block($"if ({walk.HasMoreExpression} == false)"))
                            {
                                writer.Line("yield break;");
                            }
                        }

                        if (isPage && walk.TotalPagesExpression is not null)
                        {
                            using (writer.Block($"if ({walk.TotalPagesExpression} is {{ }} totalPages_ && {parameter}_ >= totalPages_)"))
                            {
                                writer.Line("yield break;");
                            }
                        }

                        if (!isPage && walk.TotalExpression is not null)
                        {
                            using (writer.Block($"if ({walk.TotalExpression} is {{ }} total_ && {parameter}_ + count_ >= total_)"))
                            {
                                writer.Line("yield break;");
                            }
                        }

                        if (walk.Pagination.SizeParameter is { } size && walk.ItemsExpression is not null &&
                            method.Parameters.FirstOrDefault(p => ReferenceEquals(p.Source, size)) is { } sizeParameter)
                        {
                            using (writer.Block($"if ({sizeParameter.Name} is {{ }} pageSize_ && count_ < pageSize_)"))
                            {
                                writer.Line("yield break;");
                            }
                        }

                        writer.Line(isPage ? $"{parameter}_++;" : $"{parameter}_ += count_;");
                    }

                    break;
            }
        }

        if (walk.ItemsExpression is null)
        {
            return;
        }

        writer.Line();
        writer.Doc("summary", $"Enumerates every item of every page of {method.Operation.Id}.");
        WriteObsolete(writer, method);
        using (writer.Block($"public static async {Generic}.IAsyncEnumerable<{walk.ItemType}> {name}ItemsAsync({signature})"))
        {
            var pageArguments = string.Join(", ", new[] { clientName }.Concat(method.Parameters.Where(p => !ReferenceEquals(p, walk.Parameter)).Select(p => p.Name)).Concat(["cancellationToken"]));
            using (writer.Block($"await foreach (var currentPage_ in global::System.Threading.Tasks.TaskAsyncEnumerableExtensions.ConfigureAwait({name}PagesAsync({pageArguments}), false))"))
            {
                using (writer.Block($"foreach (var item_ in {walk.ItemsExpression})"))
                {
                    writer.Line("yield return item_;");
                }
            }
        }
    }

    private static string HasMoreStop(Walk walk) =>
        walk.HasMoreExpression is null ? string.Empty : $" || {walk.HasMoreExpression} == false";

    private static void WriteObsolete(CodeWriter writer, ClientEmitter.Method method)
    {
        if (method.Operation.Operation.Deprecated)
        {
            writer.Line("[global::System.Obsolete(\"This operation is deprecated by the API.\")]");
        }
    }
}
