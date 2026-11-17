using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Overlays;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Tests.Overlays;

/// <summary>Examples from RFC 9535 (JSONPath).</summary>
public class JsonPathTests
{
    private const string Store = """
        { "store": {
            "book": [
              { "category": "reference", "author": "Nigel Rees", "title": "Sayings of the Century", "price": 8.95 },
              { "category": "fiction", "author": "Evelyn Waugh", "title": "Sword of Honour", "price": 12.99 },
              { "category": "fiction", "author": "Herman Melville", "title": "Moby Dick", "isbn": "0-553-21311-3", "price": 8.99 },
              { "category": "fiction", "author": "J. R. R. Tolkien", "title": "The Lord of the Rings", "isbn": "0-395-19395-8", "price": 22.99 }
            ],
            "bicycle": { "color": "red", "price": 399 }
          }
        }
        """;

    private static DocNode Parse(string json) =>
        SyntaxParser.Parse(json, "test.json", DocumentFormat.Json, new DiagnosticBag())!;

    private static string[] Select(string query, string json = Store) =>
        JsonPath.Parse(query).Select(Parse(json)).Select(n => n.Node.ToJson()).ToArray();

    [Theory]
    [InlineData("$.store.book[*].author", "\"Nigel Rees\"", "\"Evelyn Waugh\"", "\"Herman Melville\"", "\"J. R. R. Tolkien\"")]
    [InlineData("$..author", "\"Nigel Rees\"", "\"Evelyn Waugh\"", "\"Herman Melville\"", "\"J. R. R. Tolkien\"")]
    [InlineData("$.store..price", "8.95", "12.99", "8.99", "22.99", "399")]
    [InlineData("$..book[2].author", "\"Herman Melville\"")]
    [InlineData("$..book[2].publisher")]
    [InlineData("$..book[-1].title", "\"The Lord of the Rings\"")]
    [InlineData("$..book[0,1].title", "\"Sayings of the Century\"", "\"Sword of Honour\"")]
    [InlineData("$..book[:2].title", "\"Sayings of the Century\"", "\"Sword of Honour\"")]
    [InlineData("$..book[?@.isbn].title", "\"Moby Dick\"", "\"The Lord of the Rings\"")]
    [InlineData("$..book[?@.price<10].title", "\"Sayings of the Century\"", "\"Moby Dick\"")]
    [InlineData("$['store']['bicycle']['color']", "\"red\"")]
    [InlineData("$.store.bicycle.*", "\"red\"", "399")]
    [InlineData("$..book[?@.category == 'fiction' && @.price > 20].title", "\"The Lord of the Rings\"")]
    [InlineData("$..book[?!(@.category == 'fiction')].title", "\"Sayings of the Century\"")]
    [InlineData("$..book[?@.author == $.store.book[0].author].price", "8.95")]
    [InlineData("$..book[?length(@.title) > 15].price", "8.95", "22.99")]
    [InlineData("$..book[?count(@.*) == 5].price", "8.99", "22.99")]
    [InlineData("$..book[?match(@.author, 'H.*')].price", "8.99")]
    [InlineData("$..book[?search(@.author, 'R\\\\.')].price", "22.99")]
    [InlineData("$..book[?value(@..isbn) == '0-553-21311-3'].price", "8.99")]
    public void Selects_like_the_rfc(string query, params string[] expected) =>
        Assert.Equal(expected, Select(query));

    [Theory]
    [InlineData("$[1:3]", "[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]", "\"b\"", "\"c\"")]
    [InlineData("$[5:]", "[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]", "\"f\"", "\"g\"")]
    [InlineData("$[1:5:2]", "[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]", "\"b\"", "\"d\"")]
    [InlineData("$[5:1:-2]", "[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]", "\"f\"", "\"d\"")]
    [InlineData("$[::-1]", "[\"a\",\"b\",\"c\"]", "\"c\"", "\"b\"", "\"a\"")]
    [InlineData("$[?@ == null]", "[1,null,\"x\"]", "null")]
    [InlineData("$[?@.a == @.b]", "[{\"a\":1},{\"a\":1,\"b\":1},{}]", "{\"a\":1,\"b\":1}", "{}")]
    [InlineData("$[\"a\\u0062\"]", "{\"ab\":1}", "1")]
    [InlineData(@"$['o\'k']", "{\"o'k\":2}", "2")]
    public void Handles_slices_comparisons_and_escapes(string query, string json, params string[] expected) =>
        Assert.Equal(expected, Select(query, json));

    [Theory]
    [InlineData("store")]
    [InlineData("$.")]
    [InlineData("$[01]")]
    [InlineData("$[-0]")]
    [InlineData("$[?@.a]]")]
    [InlineData("$[?@.* == 1]")]
    [InlineData("$[?length(@.*) == 1]")]
    [InlineData("$[?nope(@)]")]
    [InlineData("$[?match(@.a, 'x') == true]")]
    [InlineData("$['unterminated]")]
    [InlineData("$['o''k']")]
    public void Rejects_invalid_queries(string query) =>
        Assert.False(JsonPath.TryParse(query, out _, out var error) && error is null);

    [Fact]
    public void Reports_node_positions()
    {
        var root = Parse("""{"a":[{"b":1}]}""");
        var node = Assert.Single(JsonPath.Parse("$.a[0].b").Select(root));

        Assert.Equal("b", node.Name);
        var item = Assert.Single(JsonPath.Parse("$.a[0]").Select(root));
        Assert.Equal(0, item.Index);
        Assert.IsType<ArrayNode>(item.Parent);
    }

    [Theory]
    [InlineData("$[?match(@, ')|(.*')]")]
    [InlineData("$[?match(@, '(?i)ABC')]")]
    [InlineData(@"$[?search(@, '\\d')]")]
    [InlineData("$[?match(@, '^abc$')]")]
    public void Non_i_regexp_patterns_never_match(string query) =>
        Assert.Empty(Select(query, """["abc", "x1"]"""));

    [Fact]
    public void Lone_surrogates_compare_without_failing() =>
        Assert.Equal(["\"b\""], Select("$[?@ < 'c']", """["\ud800", "b"]"""));

    [Fact]
    public void Logical_functions_are_not_values() =>
        Assert.False(JsonPath.TryParse("$[?length(match(@.a, 'x')) > 0]", out _, out _));
}
