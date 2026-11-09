using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.References;

namespace BlazorApi.OpenApi.Tests.References;

public class ReferenceResolutionTests
{
    [Fact]
    public void Resolves_relative_file_references_from_the_file_system()
    {
        var result = TestContracts.LoadFile("external/main.yaml");

        Assert.Empty(result.Ids());
        var operation = result.Document!.Paths["/items"].Operations["get"];
        Assert.Equal("pageSize", operation.Parameters[0].Resolved.Name);
        var item = operation.Responses["200"].Content["application/json"].Schema!.Items!.Resolved;
        Assert.Equal(["id", "error"], item.Properties.Keys);
        var errorFromItem = item.Properties["error"].Resolved;
        var errorFromResponse = operation.Responses["default"].Content["application/json"].Schema!.Resolved;
        Assert.Same(errorFromResponse, errorFromItem);
        Assert.Same(errorFromItem, errorFromItem.Properties["inner"].Resolved);
        Assert.Equal("schemas/common.yaml#/schemas/Error", errorFromItem.Pointer);
        Assert.Equal(2, result.ExternalDocuments.Count);
    }

    [Fact]
    public void Resolves_external_references_from_memory()
    {
        var resolver = new InMemoryDocumentResolver()
            .Add("api/common.yaml", "Name: {type: string, maxLength: 5}");
        var main = TestContracts.Document("""
            /a:
              get:
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema: {$ref: 'common.yaml#/Name'}
            """);

        var result = OpenApiLoader.Parse(main, "api/main.yaml", new OpenApiLoadOptions { DocumentResolver = resolver });

        Assert.Empty(result.Ids());
        var schema = result.Document!.Paths["/a"].Operations["get"].Responses["200"].Content["application/json"].Schema!;
        Assert.Equal(5, schema.Resolved.MaxLength);
    }

    [Fact]
    public void Blocks_references_outside_the_reference_root()
    {
        var resolver = new InMemoryDocumentResolver().Add("secret.yaml", "A: {type: string}");
        var main = TestContracts.Document("{}", """
            schemas:
              A: {$ref: '../secret.yaml#/A'}
            """);

        var result = OpenApiLoader.Parse(main, "api/main.yaml", new OpenApiLoadOptions { DocumentResolver = resolver });

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("BAPI2002", error.Id);
        Assert.Contains("outside the reference root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Blocks_remote_references_by_default()
    {
        var main = TestContracts.Document("{}", """
            schemas:
              A: {$ref: 'https://example.com/schemas.yaml#/A'}
            """);

        var result = TestContracts.LoadYaml(main);

        Assert.Equal(["BAPI2002"], result.Ids());
    }

    [Fact]
    public void Disabled_policy_blocks_all_external_references()
    {
        var resolver = new InMemoryDocumentResolver().Add("b.yaml", "A: {type: string}");
        var main = TestContracts.Document("{}", """
            schemas:
              A: {$ref: 'b.yaml#/A'}
            """);

        var result = OpenApiLoader.Parse(main, "a.yaml", new OpenApiLoadOptions
        {
            DocumentResolver = resolver,
            ExternalReferences = ExternalReferencePolicy.Disabled,
        });

        Assert.Equal(["BAPI2002"], result.Ids());
    }

    [Fact]
    public void Reports_unresolved_local_references()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              A: {$ref: '#/components/schemas/Missing'}
            """));

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("BAPI2001", error.Id);
        Assert.Equal("#/components/schemas/A/$ref", error.Pointer);
        Assert.Contains("'Missing' does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_reference_cycles_without_hanging()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              A: {$ref: '#/components/schemas/B'}
              B: {$ref: '#/components/schemas/A'}
            """));

        Assert.All(result.Ids(), id => Assert.Equal("BAPI2003", id));
        Assert.NotEmpty(result.Ids());
    }

    [Fact]
    public void Recursive_schemas_are_not_cycles()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              Node:
                type: object
                properties:
                  children:
                    type: array
                    items: {$ref: '#/components/schemas/Node'}
            """));

        Assert.Empty(result.Ids());
        var node = result.Document!.Components.Schemas["Node"];
        Assert.Same(node, node.Properties["children"].Items!.Resolved);
    }

    [Fact]
    public void Anchor_references_are_reported_as_unsupported()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              A: {$ref: '#Thing'}
            """, "3.1.0"));

        Assert.Equal(["BAPI2005"], result.Ids());
    }

    [Fact]
    public void Missing_external_documents_are_reported()
    {
        var main = TestContracts.Document("{}", """
            schemas:
              A: {$ref: 'missing.yaml#/A'}
            """);

        var result = OpenApiLoader.Parse(main, "a.yaml", new OpenApiLoadOptions { DocumentResolver = new InMemoryDocumentResolver() });

        Assert.Equal(["BAPI1010"], result.Ids());
    }

    [Fact]
    public void Reference_chains_resolve_to_the_final_definition()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              A: {$ref: '#/components/schemas/B'}
              B: {$ref: '#/components/schemas/C'}
              C: {type: integer}
            """));

        var a = result.Document!.Components.Schemas["A"];
        Assert.Same(result.Document.Components.Schemas["B"], a.ReferenceTarget);
        Assert.Equal(JsonSchemaTypes.Integer, a.Resolved.Types);
    }

    [Theory]
    [InlineData("#/components/schemas/A~1B", "A/B")]
    [InlineData("#/components/schemas/A~0B", "A~B")]
    [InlineData("#/components/schemas/A%20B", "A B")]
    public void Json_pointer_unescapes_segments(string pointer, string key)
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", $$"""
            schemas:
              '{{key}}': {type: string}
              Ref: {$ref: '{{pointer}}'}
            """));

        Assert.Empty(result.Ids());
        Assert.Same(result.Document!.Components.Schemas[key], result.Document.Components.Schemas["Ref"].ReferenceTarget);
    }
}
