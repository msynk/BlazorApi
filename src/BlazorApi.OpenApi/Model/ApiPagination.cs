using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Model;

/// <summary>How an operation pages through results.</summary>
public enum ApiPaginationStyle
{
    /// <summary>An opaque continuation token: the response says where the next page starts.</summary>
    Cursor,

    /// <summary>A page number parameter, starting at 1.</summary>
    Page,

    /// <summary>An item offset parameter, starting at 0.</summary>
    Offset,

    /// <summary>An RFC 8288 <c>Link</c> response header with <c>rel="next"</c> whose URL carries the page parameter.</summary>
    LinkHeader,
}

/// <summary>Pagination metadata of an operation, detected from naming conventions or declared with <c>x-pagination</c>.</summary>
public sealed class ApiPagination
{
    internal ApiPagination(ApiPaginationStyle style, ApiParameter parameter, ApiResponse response, ApiTypeRef pageType)
    {
        Style = style;
        Parameter = parameter;
        Response = response;
        PageType = pageType;
    }

    /// <summary>The style.</summary>
    public ApiPaginationStyle Style { get; }

    /// <summary>The parameter that selects the page (cursor, page number or offset).</summary>
    public ApiParameter Parameter { get; }

    /// <summary>The page size parameter, when there is one.</summary>
    public ApiParameter? SizeParameter { get; internal set; }

    /// <summary>The success response that carries a page.</summary>
    public ApiResponse Response { get; }

    /// <summary>The page body type.</summary>
    public ApiTypeRef PageType { get; }

    /// <summary>
    /// The property path from the page to its items array (empty when the page is the array itself); null when the
    /// items could not be identified.
    /// </summary>
    public IReadOnlyList<string>? ItemsPath { get; internal set; }

    /// <summary>The item type, when <see cref="ItemsPath"/> is known.</summary>
    public ApiTypeRef? ItemType { get; internal set; }

    /// <summary>The property path of the next cursor (cursor style).</summary>
    public IReadOnlyList<string>? NextCursorPath { get; internal set; }

    /// <summary>The property path of a "has more" flag, when present.</summary>
    public IReadOnlyList<string>? HasMorePath { get; internal set; }

    /// <summary>The property path of the total item count, when present.</summary>
    public IReadOnlyList<string>? TotalCountPath { get; internal set; }

    /// <summary>The property path of the total page count, when present (page style).</summary>
    public IReadOnlyList<string>? TotalPagesPath { get; internal set; }

    /// <summary>Whether the pagination was declared with <c>x-pagination</c> rather than detected.</summary>
    public bool IsExplicit { get; internal set; }
}

/// <summary>Detects pagination from parameter and response property naming conventions.</summary>
internal static class PaginationDetector
{
    private static readonly string[] CursorParameters =
        ["cursor", "after", "pagetoken", "page_token", "nextpagetoken", "next_page_token", "continuationtoken", "continuation_token", "continuation", "startingafter", "starting_after", "nexttoken", "next_token", "next"];

    private static readonly string[] CursorProperties =
        ["nextcursor", "next_cursor", "nextpagetoken", "next_page_token", "continuationtoken", "continuation_token", "nexttoken", "next_token", "endcursor", "end_cursor", "cursor", "next"];

    private static readonly string[] PageParameters = ["page", "pagenumber", "page_number", "pageindex", "page_index", "pageno"];

    private static readonly string[] OffsetParameters = ["offset", "skip", "start"];

    private static readonly string[] SizeParameters = ["pagesize", "page_size", "per_page", "perpage", "limit", "size", "take", "count", "max_results", "maxresults", "top"];

    private static readonly string[] ItemsProperties = ["items", "data", "results", "values", "records", "content", "entries", "elements", "list", "rows", "nodes"];

    private static readonly string[] HasMoreProperties = ["hasmore", "has_more", "hasnext", "has_next", "hasnextpage", "has_next_page", "more"];

    private static readonly string[] TotalProperties = ["total", "totalcount", "total_count", "totalitems", "total_items", "totalelements", "total_elements", "count"];

    private static readonly string[] TotalPagesProperties = ["totalpages", "total_pages", "pagecount", "page_count", "pages"];

    private static readonly string[] Containers = ["meta", "pagination", "paging", "page", "pageinfo", "page_info", "links", "_meta"];

    public static ApiPagination? Detect(ApiOperation operation, DiagnosticBag diagnostics)
    {
        if (operation.Method != "GET")
        {
            return null;
        }

        var response = operation.SuccessResponses.FirstOrDefault(r => r.Contents.Any(c => c.Kind == ApiBodyKind.Json && c.Type is not null));
        if (response is null)
        {
            return null;
        }

        var pageType = response.Contents.First(c => c.Kind == ApiBodyKind.Json && c.Type is not null).Type!;
        if (operation.Source.TryGetExtension("x-pagination", out var declared))
        {
            return Explicit(operation, response, pageType, declared, diagnostics);
        }

        var query = operation.Parameters.Where(p => p.Location == ParameterLocation.Query).ToList();
        var size = Find(query, SizeParameters, p => IsInteger(p.Type));
        ApiPagination? pagination = null;

        var cursor = Find(query, CursorParameters, p => IsString(p.Type));
        var nextCursor = FindPath(pageType, CursorProperties, IsString);
        if (cursor is not null && nextCursor is not null)
        {
            pagination = new ApiPagination(ApiPaginationStyle.Cursor, cursor, response, pageType) { NextCursorPath = nextCursor };
        }

        var hasLink = response.Headers.Any(h => string.Equals(h.Name, "Link", StringComparison.OrdinalIgnoreCase));
        if (pagination is null && hasLink && (Find(query, PageParameters, p => IsInteger(p.Type)) ?? cursor) is { } linkParameter)
        {
            pagination = new ApiPagination(ApiPaginationStyle.LinkHeader, linkParameter, response, pageType);
        }

        if (pagination is null && Find(query, PageParameters, p => IsInteger(p.Type)) is { } page)
        {
            pagination = new ApiPagination(ApiPaginationStyle.Page, page, response, pageType)
            {
                TotalPagesPath = FindPath(pageType, TotalPagesProperties, IsInteger),
            };
        }

        if (pagination is null && Find(query, OffsetParameters, p => IsInteger(p.Type)) is { } offset)
        {
            pagination = new ApiPagination(ApiPaginationStyle.Offset, offset, response, pageType);
        }

        if (pagination is null)
        {
            return null;
        }

        pagination.SizeParameter = ReferenceEquals(size, pagination.Parameter) ? null : size;
        pagination.HasMorePath = FindPath(pageType, HasMoreProperties, IsBoolean);
        pagination.TotalCountPath = FindPath(pageType, TotalProperties, IsInteger);
        SetItems(pagination, pageType, null);
        return pagination;
    }

    private static ApiPagination? Explicit(ApiOperation operation, ApiResponse response, ApiTypeRef pageType, DocNode declared, DiagnosticBag diagnostics)
    {
        var location = operation.Source.Location;
        var pointer = operation.Source.Pointer + "/x-pagination";
        if (declared is not ObjectNode config)
        {
            diagnostics.Report(DiagnosticDescriptors.InvalidValue, declared.Location, pointer, declared.ToJson(), "x-pagination", "expected an object");
            return null;
        }

        string? Text(string name) => config[name] is ScalarNode { IsNull: false } s ? s.Text : null;
        IReadOnlyList<string>? Path(string name) => Text(name) is { } value ? value.Split('.') : null;

        var styleText = Text("style");
        ApiPaginationStyle? style = styleText switch
        {
            "cursor" => ApiPaginationStyle.Cursor,
            "page" => ApiPaginationStyle.Page,
            "offset" => ApiPaginationStyle.Offset,
            "link" or "linkHeader" => ApiPaginationStyle.LinkHeader,
            _ => null,
        };
        var parameterName = Text("parameter");
        var parameter = operation.Parameters.FirstOrDefault(p => p.Location == ParameterLocation.Query && p.Name == parameterName);
        if (style is null || parameter is null)
        {
            diagnostics.Report(DiagnosticDescriptors.InvalidValue, config.Location, pointer, config.ToJson(), "x-pagination",
                "expected 'style' (cursor, page, offset or link) and 'parameter' naming a query parameter of the operation");
            return null;
        }

        var pagination = new ApiPagination(style.Value, parameter, response, pageType)
        {
            IsExplicit = true,
            NextCursorPath = Path("next"),
            HasMorePath = Path("hasMore"),
            TotalCountPath = Path("total"),
            TotalPagesPath = Path("totalPages"),
        };
        if (Text("size") is { } sizeName)
        {
            pagination.SizeParameter = operation.Parameters.FirstOrDefault(p => p.Location == ParameterLocation.Query && p.Name == sizeName);
        }

        if (style == ApiPaginationStyle.Cursor && (pagination.NextCursorPath is null || Resolve(pageType, pagination.NextCursorPath) is null))
        {
            diagnostics.Report(DiagnosticDescriptors.InvalidValue, config.Location, pointer, Text("next") ?? "(missing)", "x-pagination.next",
                "cursor pagination needs 'next': the response property path of the next cursor");
            return null;
        }

        SetItems(pagination, pageType, Path("items"));
        return pagination;
    }

    private static void SetItems(ApiPagination pagination, ApiTypeRef pageType, IReadOnlyList<string>? declared)
    {
        if (declared is not null)
        {
            if (Resolve(pageType, declared) is ApiArrayType declaredArray)
            {
                pagination.ItemsPath = declared;
                pagination.ItemType = declaredArray.Items;
            }

            return;
        }

        if (pageType is ApiArrayType array)
        {
            pagination.ItemsPath = [];
            pagination.ItemType = array.Items;
            return;
        }

        if (pageType is not ApiNamedTypeRef { Target: ApiObjectType obj })
        {
            return;
        }

        var arrays = obj.AllProperties.Where(p => p.Type is ApiArrayType).ToList();
        var items = arrays.FirstOrDefault(p => ItemsProperties.Contains(p.JsonName.ToLowerInvariant())) ?? (arrays.Count == 1 ? arrays[0] : null);
        if (items is not null)
        {
            pagination.ItemsPath = [items.JsonName];
            pagination.ItemType = ((ApiArrayType)items.Type).Items;
        }
    }

    private static ApiParameter? Find(List<ApiParameter> parameters, string[] names, Func<ApiParameter, bool> filter)
    {
        foreach (var name in names)
        {
            var match = parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && filter(p));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Finds a top-level property (or one inside a conventional container such as <c>meta</c>) by name.</summary>
    private static IReadOnlyList<string>? FindPath(ApiTypeRef type, string[] names, Func<ApiTypeRef, bool> filter)
    {
        if (type is not ApiNamedTypeRef { Target: ApiObjectType obj })
        {
            return null;
        }

        foreach (var name in names)
        {
            var property = obj.AllProperties.FirstOrDefault(p => string.Equals(p.JsonName, name, StringComparison.OrdinalIgnoreCase) && filter(p.Type));
            if (property is not null)
            {
                return [property.JsonName];
            }
        }

        foreach (var container in obj.AllProperties.Where(p => Containers.Contains(p.JsonName.ToLowerInvariant()) && p.Type is ApiNamedTypeRef { Target: ApiObjectType }))
        {
            var inner = ((ApiObjectType)((ApiNamedTypeRef)container.Type).Target).AllProperties;
            foreach (var name in names)
            {
                var property = inner.FirstOrDefault(p => string.Equals(p.JsonName, name, StringComparison.OrdinalIgnoreCase) && filter(p.Type));
                if (property is not null)
                {
                    return [container.JsonName, property.JsonName];
                }
            }
        }

        return null;
    }

    /// <summary>Resolves a property path to its type.</summary>
    public static ApiTypeRef? Resolve(ApiTypeRef type, IReadOnlyList<string> path)
    {
        var current = type;
        foreach (var segment in path)
        {
            if (current is not ApiNamedTypeRef { Target: ApiObjectType obj } ||
                obj.AllProperties.FirstOrDefault(p => p.JsonName == segment) is not { } property)
            {
                return null;
            }

            current = property.Type;
        }

        return current;
    }

    private static bool IsString(ApiParameter parameter) => IsString(parameter.Type);

    private static bool IsString(ApiTypeRef type) => type is ApiPrimitiveType { Kind: ApiPrimitiveKind.String };

    private static bool IsInteger(ApiTypeRef type) => type is ApiPrimitiveType { Kind: ApiPrimitiveKind.Integer };

    private static bool IsBoolean(ApiTypeRef type) => type is ApiPrimitiveType { Kind: ApiPrimitiveKind.Boolean };
}
