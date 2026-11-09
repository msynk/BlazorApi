using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Tests.Syntax;

public class YamlParserTests
{
    private static DocNode Parse(string yaml)
    {
        var diagnostics = new DiagnosticBag();
        var node = SyntaxParser.Parse(yaml.Replace("\r\n", "\n"), "test.yaml", DocumentFormat.Yaml, diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        return node!;
    }

    private static string Json(string yaml) => Parse(yaml).ToJson();

    private static ApiDiagnostic ParseError(string yaml, ParserLimits? limits = null)
    {
        var diagnostics = new DiagnosticBag();
        var node = SyntaxParser.Parse(yaml.Replace("\r\n", "\n"), "test.yaml", DocumentFormat.Yaml, diagnostics, limits);
        Assert.Null(node);
        return Assert.Single(diagnostics, d => d.IsError);
    }

    [Fact]
    public void Parses_nested_block_mappings_and_sequences()
    {
        const string yaml = """
            openapi: 3.1.0
            info:
              title: Pets
              version: 1.0.0
            tags:
              - name: pets
                description: Pet operations
              - name: store
            servers:
            - url: https://example.com/v1
            numbers: [1, 2.5, -3]
            """;

        Assert.Equal(
            """{"openapi":"3.1.0","info":{"title":"Pets","version":"1.0.0"},"tags":[{"name":"pets","description":"Pet operations"},{"name":"store"}],"servers":[{"url":"https://example.com/v1"}],"numbers":[1,2.5,-3]}""",
            Json(yaml));
    }

    [Fact]
    public void Resolves_core_schema_scalars()
    {
        const string yaml = """
            a: null
            b: ~
            c:
            d: true
            e: False
            f: 42
            g: -0.5
            h: 1e3
            i: 0x1F
            j: 0o17
            k: +12
            l: 007
            m: yes
            n: 1.0.0
            o: .5
            """;
        var node = (ObjectNode)Parse(yaml);

        Assert.Equal(
            """{"a":null,"b":null,"c":null,"d":true,"e":false,"f":42,"g":-0.5,"h":1e3,"i":31,"j":15,"k":12,"l":7,"m":"yes","n":"1.0.0","o":0.5}""",
            node.ToJson());
        Assert.Equal("007", ((ScalarNode)node["l"]!).Text);
        Assert.Equal("0x1F", ((ScalarNode)node["i"]!).Text);
    }

    [Fact]
    public void Numeric_looking_keys_are_strings()
    {
        var node = (ObjectNode)Parse("""
            responses:
              200:
                description: OK
              '404':
                description: Missing
            """);

        Assert.Equal(["200", "404"], ((ObjectNode)node["responses"]!).Properties.Select(p => p.Name));
    }

    [Fact]
    public void Parses_quoted_scalars_with_escapes_and_folding()
    {
        var node = (ObjectNode)Parse("""
            single: 'it''s # not a comment'
            double: "tab\there \x41\U0001F600 \"q\""
            folded: "first
              second

              third"
            escaped: "no\
              space"
            """);

        Assert.Equal("it's # not a comment", ((ScalarNode)node["single"]!).Value);
        Assert.Equal("tab\there A\U0001F600 \"q\"", ((ScalarNode)node["double"]!).Value);
        Assert.Equal("first second\nthird", ((ScalarNode)node["folded"]!).Value);
        Assert.Equal("nospace", ((ScalarNode)node["escaped"]!).Value);
    }

    [Fact]
    public void Parses_block_scalars_with_chomping()
    {
        var node = (ObjectNode)Parse("""
            literal: |
              line one
                indented
              line three

            folded: >
              folded one
              folded two

              new paragraph
                kept indented
              back
            strip: |-
              text

            keep: |+
              text

            explicit: |2
                four spaces
            after: end
            """);

        Assert.Equal("line one\n  indented\nline three\n", ((ScalarNode)node["literal"]!).Value);
        Assert.Equal("folded one folded two\nnew paragraph\n  kept indented\nback\n", ((ScalarNode)node["folded"]!).Value);
        Assert.Equal("text", ((ScalarNode)node["strip"]!).Value);
        Assert.Equal("text\n\n", ((ScalarNode)node["keep"]!).Value);
        Assert.Equal("  four spaces\n", ((ScalarNode)node["explicit"]!).Value);
        Assert.Equal("end", ((ScalarNode)node["after"]!).Value);
    }

    [Fact]
    public void Parses_multiline_plain_scalars()
    {
        var node = (ObjectNode)Parse("""
            description: This is a long
              description that continues

              with a paragraph # and a comment
            next: value
            """);

        Assert.Equal("This is a long description that continues\nwith a paragraph", ((ScalarNode)node["description"]!).Value);
        Assert.Equal("value", ((ScalarNode)node["next"]!).Value);
    }

    [Fact]
    public void Parses_flow_collections_across_lines()
    {
        const string yaml = """
            required: [id, name]
            example: {"id": 1, name: Rex, tags: [a, "b c"], empty: {}, none: []}
            multi: [
              one,  # comment
              two
            ]
            json: {"a":1,"b":[true,null]}
            """;

        Assert.Equal(
            """{"required":["id","name"],"example":{"id":1,"name":"Rex","tags":["a","b c"],"empty":{},"none":[]},"multi":["one","two"],"json":{"a":1,"b":[true,null]}}""",
            Json(yaml));
    }

    [Fact]
    public void Handles_comments_blank_lines_and_document_markers()
    {
        const string yaml = """
            %YAML 1.2
            ---
            # leading comment

            a: 1 # trailing
              # indented comment line
            b:   # comment after key
              c: 2
            url: http://example.com/a#fragment
            ...
            """;

        Assert.Equal("""{"a":1,"b":{"c":2},"url":"http://example.com/a#fragment"}""", Json(yaml));
    }

    [Fact]
    public void Resolves_anchors_aliases_and_merge_keys()
    {
        const string yaml = """
            base: &base
              type: object
              description: Base
            other: *base
            derived:
              <<: *base
              description: Derived
            list:
              - &item {a: 1}
              - *item
            """;

        Assert.Equal(
            """{"base":{"type":"object","description":"Base"},"other":{"type":"object","description":"Base"},"derived":{"description":"Derived","type":"object"},"list":[{"a":1},{"a":1}]}""",
            Json(yaml));
    }

    [Fact]
    public void Applies_core_tags()
    {
        var node = (ObjectNode)Parse("""
            a: !!str 123
            b: !!int "42"
            c: !custom value
            """);

        Assert.Equal(NodeKind.String, node["a"]!.Kind);
        Assert.Equal("123", ((ScalarNode)node["a"]!).Value);
        Assert.Equal(NodeKind.Number, node["b"]!.Kind);
        Assert.Equal("value", ((ScalarNode)node["c"]!).Value);
    }

    [Fact]
    public void Parses_compact_nested_sequences_and_mappings()
    {
        const string yaml = """
            - - a
              - b
            - key: v
              nested:
                - x
            -
              late: 1
            -
            """;

        Assert.Equal("""[["a","b"],{"key":"v","nested":["x"]},{"late":1},null]""", Json(yaml));
    }

    [Fact]
    public void Records_positions()
    {
        var node = (ObjectNode)Parse("""
            a:
              b: 1
            """);
        var b = ((ObjectNode)node["a"]!).GetProperty("b")!;

        Assert.Equal(2, b.KeyLocation.Line);
        Assert.Equal(3, b.KeyLocation.Column);
        Assert.Equal(2, b.Value.Location.Line);
        Assert.Equal(6, b.Value.Location.Column);
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        var diagnostics = new DiagnosticBag();
        var node = SyntaxParser.Parse("a: 1\r\nb:\r\n  - x\r\n  - y\r\n", "crlf.yaml", DocumentFormat.Yaml, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal("""{"a":1,"b":["x","y"]}""", node!.ToJson());
    }

    [Fact]
    public void Empty_document_is_null()
    {
        Assert.Equal(NodeKind.Null, Parse("# only a comment\n").Kind);
    }

    [Theory]
    [InlineData("a: b: c", "Mapping values are not allowed")]
    [InlineData("a:\n\tb: 1", "Tabs are not allowed")]
    [InlineData("a: 1\n  b: 2", "Mapping values are not allowed")]
    [InlineData("a: \"x\"\n  b: 2", "Unexpected indentation")]
    [InlineData("a: \"unterminated", "Unterminated double-quoted")]
    [InlineData("a: *missing", "undefined anchor")]
    [InlineData("a: [1, 2", "Unterminated flow sequence")]
    [InlineData("a: 1\n---\nb: 2", "Multiple YAML documents")]
    [InlineData("a:\n  - 1\n  b: 2", "Unexpected")]
    public void Reports_syntax_errors(string yaml, string expected)
    {
        var error = ParseError(yaml);

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.True(error.Location.Line >= 1);
    }

    [Theory]
    [InlineData("? complex\n: value")]
    [InlineData("[a, b]: value")]
    [InlineData("a: &x\n  b: *x")]
    public void Reports_unsupported_features(string yaml)
    {
        Assert.Equal("BAPI1011", ParseError(yaml).Id);
    }

    [Fact]
    public void Rejects_alias_expansion_attacks()
    {
        const string yaml = """
            a: &a [x, x, x, x, x, x, x, x, x, x]
            b: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a, *a]
            c: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b, *b]
            d: &d [*c, *c, *c, *c, *c, *c, *c, *c, *c, *c]
            e: &e [*d, *d, *d, *d, *d, *d, *d, *d, *d, *d]
            """;

        var error = ParseError(yaml, new ParserLimits { MaxExpandedNodes = 10_000 });

        Assert.Equal("BAPI1002", error.Id);
    }

    [Fact]
    public void Enforces_depth_limit()
    {
        var yaml = new string('[', 40) + new string(']', 40);

        Assert.Equal("BAPI1002", ParseError(yaml, new ParserLimits { MaxDepth = 8 }).Id);
    }

    [Fact]
    public void Auto_detects_format_from_content_and_extension()
    {
        Assert.Equal(DocumentFormat.Json, SyntaxParser.DetectFormat("  {\"a\":1}", null));
        Assert.Equal(DocumentFormat.Yaml, SyntaxParser.DetectFormat("a: 1", null));
        Assert.Equal(DocumentFormat.Yaml, SyntaxParser.DetectFormat("{a: 1}", "spec.yml"));
        Assert.Equal(DocumentFormat.Json, SyntaxParser.DetectFormat("a: 1", "https://x/spec.json?v=1"));
    }

    [Fact]
    public void Json_looking_yaml_falls_back_to_yaml_when_extension_is_unknown()
    {
        var diagnostics = new DiagnosticBag();

        var node = SyntaxParser.Parse("{a: 1, b: [x]}", "spec", DocumentFormat.Auto, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal("""{"a":1,"b":["x"]}""", node!.ToJson());
    }

    [Fact]
    public void Json_documents_parse_identically_as_yaml()
    {
        const string json = """
            {
              "openapi": "3.0.3",
              "info": { "title": "T", "version": "1" },
              "paths": { "/a": { "get": { "responses": { "200": { "description": "ok" } } } } },
              "x-list": [1, 2.5, true, null, "s"]
            }
            """;
        var diagnostics = new DiagnosticBag();

        var asJson = SyntaxParser.Parse(json, "a.json", DocumentFormat.Json, diagnostics)!;
        var asYaml = SyntaxParser.Parse(json, "a.yaml", DocumentFormat.Yaml, diagnostics)!;

        Assert.Empty(diagnostics);
        Assert.Equal(asJson.ToJson(), asYaml.ToJson());
    }
}
