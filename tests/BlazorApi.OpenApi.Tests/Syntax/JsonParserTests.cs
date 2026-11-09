using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Tests.Syntax;

public class JsonParserTests
{
    private static DocNode Parse(string json)
    {
        var diagnostics = new DiagnosticBag();
        var node = SyntaxParser.Parse(json, "test.json", DocumentFormat.Json, diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        return node!;
    }

    private static ApiDiagnostic ParseError(string json)
    {
        var diagnostics = new DiagnosticBag();
        var node = SyntaxParser.Parse(json, "test.json", DocumentFormat.Json, diagnostics);
        Assert.Null(node);
        return Assert.Single(diagnostics, d => d.IsError);
    }

    [Fact]
    public void Parses_all_value_kinds_and_preserves_key_order()
    {
        var node = Assert.IsType<ObjectNode>(Parse("""{"z":1,"a":"x","m":[true,false,null],"n":{"k":-1.5e3}}"""));

        Assert.Equal(["z", "a", "m", "n"], node.Properties.Select(p => p.Name));
        Assert.Equal(NodeKind.Number, node["z"]!.Kind);
        Assert.Equal("x", ((ScalarNode)node["a"]!).Value);
        var array = Assert.IsType<ArrayNode>(node["m"]);
        Assert.Equal([NodeKind.Boolean, NodeKind.Boolean, NodeKind.Null], array.Items.Select(i => i.Kind));
        Assert.Equal("-1.5e3", ((ScalarNode)((ObjectNode)node["n"]!)["k"]!).Value);
    }

    [Fact]
    public void Keeps_large_numbers_exact()
    {
        var node = (ObjectNode)Parse("""{"big":123456789012345678901234567890,"dec":0.1000000000000000000001}""");

        Assert.Equal("123456789012345678901234567890", ((ScalarNode)node["big"]!).Value);
        Assert.Equal("0.1000000000000000000001", ((ScalarNode)node["dec"]!).Value);
    }

    [Fact]
    public void Decodes_escapes_including_surrogate_pairs()
    {
        var node = (ScalarNode)Parse("""
            "a\"b\\c\/d\n\t\u00e9\ud83d\ude00"
            """);

        Assert.Equal("a\"b\\c/d\n\t\u00e9\U0001F600", node.Value);
    }

    [Fact]
    public void Records_one_based_line_and_column()
    {
        var node = (ObjectNode)Parse("{\n  \"a\": {\n    \"b\": 1\n  }\n}");
        var inner = (ObjectNode)node["a"]!;

        Assert.Equal(2, inner.Location.Line);
        Assert.Equal(8, inner.Location.Column);
        Assert.Equal(3, inner.GetProperty("b")!.KeyLocation.Line);
        Assert.Equal(5, inner.GetProperty("b")!.KeyLocation.Column);
        Assert.Equal("test.json", inner.Location.Source);
    }

    [Fact]
    public void Duplicate_keys_warn_and_last_value_wins()
    {
        var diagnostics = new DiagnosticBag();
        var node = (ObjectNode)SyntaxParser.Parse("""{"a":1,"a":2}""", "x.json", DocumentFormat.Json, diagnostics)!;

        Assert.Equal("2", ((ScalarNode)node["a"]!).Value);
        Assert.Equal(1, node.Count);
        var warning = Assert.Single(diagnostics);
        Assert.Equal("BAPI1005", warning.Id);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    [Theory]
    [InlineData("{\"a\":1,}", "Trailing comma")]
    [InlineData("[1,]", "Trailing comma")]
    [InlineData("{\"a\" 1}", "':' was expected")]
    [InlineData("{a:1}", "property name in double quotes")]
    [InlineData("01", "Leading zeros")]
    [InlineData("1.", "decimal point")]
    [InlineData("\"abc", "Unterminated string")]
    [InlineData("\"a\\qb\"", "Invalid escape")]
    [InlineData("{} x", "after the end")]
    [InlineData("", "empty")]
    [InlineData("tru", "Unexpected")]
    public void Reports_syntax_errors_with_position(string json, string expected)
    {
        var error = ParseError(json);

        Assert.Equal("BAPI1001", error.Id);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.True(error.Location.Line >= 1);
    }

    [Fact]
    public void Enforces_depth_limit()
    {
        var json = new string('[', 50) + new string(']', 50);
        var diagnostics = new DiagnosticBag();

        var node = SyntaxParser.Parse(json, "deep.json", DocumentFormat.Json, diagnostics, new ParserLimits { MaxDepth = 10 });

        Assert.Null(node);
        Assert.Equal("BAPI1002", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void Enforces_document_length_limit()
    {
        var diagnostics = new DiagnosticBag();

        var node = SyntaxParser.Parse("{\"a\":1}", "x.json", DocumentFormat.Json, diagnostics, new ParserLimits { MaxDocumentLength = 3 });

        Assert.Null(node);
        Assert.Equal("BAPI1002", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void Skips_byte_order_mark()
    {
        var node = Parse((char)0xFEFF + "{\"a\":1}");

        Assert.Equal(NodeKind.Object, node.Kind);
    }

    [Fact]
    public void ToJson_round_trips_compactly()
    {
        const string json = """{"a":[1,"two",{"b":null}],"c":true,"d":"q\"uote"}""";

        Assert.Equal(json, Parse(json).ToJson());
    }

    [Fact]
    public void DeepEquals_ignores_key_order_and_number_spelling()
    {
        var left = Parse("""{"a":1.0,"b":[1,2]}""");
        var right = Parse("""{"b":[1,2],"a":1}""");
        var different = Parse("""{"b":[2,1],"a":1}""");

        Assert.True(DocNode.DeepEquals(left, right));
        Assert.False(DocNode.DeepEquals(left, different));
    }
}
