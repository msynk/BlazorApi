using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Overlays;

namespace BlazorApi.OpenApi.Tests.Overlays;

public class OverlayTests
{
    private const string Contract = """
        openapi: 3.1.0
        info: { title: Shop, version: '1' }
        tags: [{ name: internal }]
        paths:
          /items:
            get:
              operationId: listItems
              tags: [public]
              responses:
                '200': { description: ok }
          /admin:
            get:
              operationId: purge
              tags: [internal]
              responses:
                '200': { description: ok }
        components:
          schemas:
            Item:
              type: object
              required: [id]
              properties:
                id: { type: integer }
                name: { type: string }
        """;

    private static OpenApiLoadResult Load(params string[] overlays) =>
        OpenApiLoader.Parse(Contract, "shop.yaml", new OpenApiLoadOptions
        {
            Overlays = overlays.Select((text, i) => new OpenApiOverlay($"overlay{i}.yaml", text)).ToArray(),
        });

    [Fact]
    public void Updates_merge_concatenate_and_replace()
    {
        var result = Load("""
            overlay: 1.0.0
            info: { title: Fixes, version: '1' }
            actions:
              - target: $.info
                update: { title: Shop API, description: Patched. }
              - target: $.components.schemas.Item
                update:
                  required: [name]
                  properties:
                    name: { maxLength: 40 }
                    price: { type: number }
              - target: $.paths.*.get
                update: { x-checked: true }
              - target: $.tags
                update: { name: extra }
            """);

        Assert.False(result.HasErrors, result.Describe());
        var document = result.Document!;
        Assert.Equal("Shop API", document.Info.Title);
        Assert.Equal("Patched.", document.Info.Description);
        var item = document.Components.Schemas["Item"];
        Assert.Equal(["id", "name"], item.Required);
        Assert.Equal(40, item.Properties["name"].MaxLength);
        Assert.True(item.Properties.ContainsKey("price"));
        Assert.Equal(["internal", "extra"], document.Tags.Select(t => t.Name));
        Assert.All(document.Paths.Values, p => Assert.True(p.Operations.Values.Single().Extensions.ContainsKey("x-checked")));
    }

    [Fact]
    public void Removes_filtered_nodes_and_copies_values()
    {
        var result = Load("""
            overlay: 1.1.0
            info: { title: Public only, version: '1' }
            actions:
              - target: $.paths[?@.get.tags[0] == 'internal']
                remove: true
              - target: $.components.schemas.Item.properties.name
                copy: $.components.schemas.Item.properties.id
              - target: $.paths['/missing']
                remove: true
            """);

        Assert.False(result.HasErrors, result.Describe());
        Assert.Equal(["/items"], result.Document!.Paths.Keys);
        Assert.Equal(JsonSchemaTypes.Integer, result.Document.Components.Schemas["Item"].Properties["name"].Types & JsonSchemaTypes.Integer);
        Assert.Contains("BAPI7003", result.Ids());
    }

    [Fact]
    public void Primitive_values_are_replaced_and_overlays_apply_in_order()
    {
        var result = Load(
            """
            overlay: 1.1.0
            info: { title: A, version: '1' }
            actions:
              - target: $.info.version
                update: '2.0'
            """,
            """
            overlay: 1.1.0
            info: { title: B, version: '1' }
            actions:
              - target: $.info[?@ == '2.0']
                update: '3.0'
            """);

        Assert.False(result.HasErrors, result.Describe());
        Assert.Equal("3.0", result.Document!.Info.Version);
    }

    [Fact]
    public void Reusable_actions_are_resolved()
    {
        var result = Load("""
            overlay: 1.2.0
            info: { title: Reuse, version: '1' }
            components:
              actions:
                markBeta:
                  fields:
                    update: { x-beta: true }
            actions:
              - $ref: '#/components/actions/markBeta'
                target: $.paths['/items'].get
            """);

        Assert.False(result.HasErrors, result.Describe());
        Assert.True(result.Document!.Paths["/items"].Operations.Values.Single().Extensions.ContainsKey("x-beta"));
    }

    [Theory]
    [InlineData("overlay: 2.0.0\ninfo: { title: X, version: '1' }\nactions: [{ target: $.info, update: {} }]", "BAPI7001")]
    [InlineData("overlay: 1.0.0\ninfo: { title: X, version: '1' }\nactions: []", "BAPI7001")]
    [InlineData("overlay: 1.0.0\ninfo: { title: X, version: '1' }\nactions: [{ target: '$.info[', update: {} }]", "BAPI7002")]
    [InlineData("overlay: 1.0.0\ninfo: { title: X, version: '1' }\nactions: [{ target: $.info, update: [1] }]", "BAPI7004")]
    [InlineData("overlay: 1.0.0\ninfo: { title: X, version: '1' }\nactions: [{ target: $.info, update: { title: [1] } }]", "BAPI7004")]
    [InlineData("overlay: 1.0.0\ninfo: { title: X, version: '1' }\nactions: [{ target: $.info }]", "BAPI7001")]
    [InlineData("overlay: 1.0.0\ninfo: { title: X, version: '1' }\nactions: [{ target: $, remove: true }]", "BAPI7004")]
    [InlineData("overlay: 1.1.0\ninfo: { title: X, version: '1' }\nactions: [{ target: $.info, copy: $.paths.* }]", "BAPI7004")]
    public void Invalid_overlays_are_reported(string overlay, string id)
    {
        var result = Load(overlay);

        Assert.True(result.HasErrors);
        Assert.Contains(id, result.Ids());
        Assert.Null(result.Document);
    }

    [Fact]
    public void Newer_versions_warn_and_the_original_tree_is_unchanged()
    {
        var overlay = new OpenApiOverlay("o.yaml", "overlay: 1.9.0\ninfo: { title: X, version: '1' }\nactions: [{ target: $.info, update: { title: New } }]");
        var options = new OpenApiLoadOptions { Overlays = [overlay] };

        var patched = OpenApiLoader.Parse(Contract, "shop.yaml", options);
        var plain = OpenApiLoader.Parse(Contract, "shop.yaml");

        Assert.Contains("BAPI7005", patched.Ids());
        Assert.Equal("New", patched.Document!.Info.Title);
        Assert.Equal("Shop", plain.Document!.Info.Title);
    }

    [Fact]
    public void Overlays_cannot_grow_the_document_without_bound()
    {
        var lines = new List<string> { "overlay: 1.1.0", "info: { title: G, version: '1' }", "actions:" };
        lines.AddRange(Enumerable.Repeat("  - { target: $.tags, copy: $.tags }", 40));
        var result = OpenApiLoader.Parse(Contract, "shop.yaml", new OpenApiLoadOptions
        {
            Overlays = [new OpenApiOverlay("grow.yaml", string.Join(Environment.NewLine, lines))],
            Limits = BlazorApi.OpenApi.Syntax.ParserLimits.Default with { MaxExpandedNodes = 10_000 },
        });

        Assert.Contains("BAPI1002", result.Ids());
        Assert.Null(result.Document);
    }
}
