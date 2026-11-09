using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.OpenApi.Tests.Model;

public class PaginationDetectorTests
{
    private static (ApiPagination? Pagination, IReadOnlyList<ApiDiagnostic> Diagnostics) Detect(string operationYaml, string components)
    {
        var yaml = TestContracts.Document("/items:\n  get:\n" + string.Join("\n", operationYaml.Replace("\r\n", "\n").Trim('\n').Split('\n').Select(l => "    " + l)), components);
        var result = TestContracts.LoadYaml(yaml);
        Assert.False(result.HasErrors, result.Describe());
        var diagnostics = new DiagnosticBag();
        var model = ApiModelBuilder.Build(result.Document!, diagnostics);
        return (model.Operations[0].Pagination, diagnostics);
    }

    private const string Response = """
        responses:
          '200':
            description: ok
            content:
              application/json:
                schema: {$ref: '#/components/schemas/Page'}
        """;

    [Fact]
    public void Detects_cursor_pagination_with_nested_cursor()
    {
        var (pagination, _) = Detect("parameters:\n  - {name: pageToken, in: query, schema: {type: string}}\n  - {name: limit, in: query, schema: {type: integer}}\n" + Response, """
            schemas:
              Page:
                type: object
                properties:
                  data: {type: array, items: {type: string}}
                  meta:
                    type: object
                    properties:
                      next_cursor: {type: string, nullable: true}
            """);

        Assert.NotNull(pagination);
        Assert.Equal(ApiPaginationStyle.Cursor, pagination!.Style);
        Assert.Equal("pageToken", pagination.Parameter.Name);
        Assert.Equal("limit", pagination.SizeParameter!.Name);
        Assert.Equal(["meta", "next_cursor"], pagination.NextCursorPath);
        Assert.Equal(["data"], pagination.ItemsPath);
        Assert.Equal("string", pagination.ItemType!.ToString());
    }

    [Fact]
    public void Detects_page_number_pagination_with_totals()
    {
        var (pagination, _) = Detect("parameters:\n  - {name: page, in: query, schema: {type: integer}}\n  - {name: per_page, in: query, schema: {type: integer}}\n" + Response, """
            schemas:
              Page:
                type: object
                properties:
                  results: {type: array, items: {type: integer}}
                  total_pages: {type: integer}
                  has_more: {type: boolean}
            """);

        Assert.Equal(ApiPaginationStyle.Page, pagination!.Style);
        Assert.Equal(["total_pages"], pagination.TotalPagesPath);
        Assert.Equal(["has_more"], pagination.HasMorePath);
        Assert.Equal("per_page", pagination.SizeParameter!.Name);
    }

    [Fact]
    public void Detects_offset_pagination_of_top_level_arrays()
    {
        var (pagination, _) = Detect("""
            parameters:
              - {name: offset, in: query, schema: {type: integer}}
              - {name: limit, in: query, schema: {type: integer}}
            responses:
              '200':
                description: ok
                content:
                  application/json:
                    schema: {type: array, items: {type: string}}
            """, "{}");

        Assert.Equal(ApiPaginationStyle.Offset, pagination!.Style);
        Assert.Empty(pagination.ItemsPath!);
    }

    [Fact]
    public void Detects_link_header_pagination()
    {
        var (pagination, _) = Detect("""
            parameters:
              - {name: page, in: query, schema: {type: integer}}
            responses:
              '200':
                description: ok
                headers:
                  Link: {schema: {type: string}}
                content:
                  application/json:
                    schema: {type: array, items: {type: string}}
            """, "{}");

        Assert.Equal(ApiPaginationStyle.LinkHeader, pagination!.Style);
    }

    [Fact]
    public void Honors_explicit_x_pagination_and_reports_invalid_declarations()
    {
        var (pagination, _) = Detect("parameters:\n  - {name: from, in: query, schema: {type: string}}\nx-pagination: {style: cursor, parameter: from, next: links.after, items: entries}\n" + Response, """
            schemas:
              Page:
                type: object
                properties:
                  entries: {type: array, items: {type: string}}
                  links:
                    type: object
                    properties: {after: {type: string}}
            """);
        var (invalid, diagnostics) = Detect("x-pagination: {style: cursor, parameter: missing}\n" + Response, """
            schemas:
              Page: {type: object, properties: {a: {type: string}}}
            """);

        Assert.True(pagination!.IsExplicit);
        Assert.Equal(["links", "after"], pagination.NextCursorPath);
        Assert.Equal(["entries"], pagination.ItemsPath);
        Assert.Null(invalid);
        Assert.Equal("BAPI1009", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void Ignores_operations_without_pagination_signals()
    {
        var (pagination, _) = Detect("parameters:\n  - {name: filter, in: query, schema: {type: string}}\n" + Response, """
            schemas:
              Page: {type: object, properties: {items: {type: array, items: {type: string}}}}
            """);

        Assert.Null(pagination);
    }
}
