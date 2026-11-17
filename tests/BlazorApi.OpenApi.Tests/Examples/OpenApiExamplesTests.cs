using BlazorApi.OpenApi.Examples;

namespace BlazorApi.OpenApi.Tests.Examples;

public class OpenApiExamplesTests
{
    private const string Paths = """
        /a:
          get:
            responses:
              '200':
                description: ok
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/Shelf' }
                    examples:
                      empty: { value: { pets: [] } }
        """;

    private const string Components = """
        schemas:
          Shelf:
            type: object
            properties:
              pets: { type: array, items: { $ref: '#/components/schemas/Pet' } }
              owner: { $ref: '#/components/schemas/Person' }
              size: { type: integer, minimum: 3 }
              code: { type: string, format: uuid }
              secret: { type: string, writeOnly: true }
              state: { type: string, enum: [open, closed] }
          Pet:
            type: object
            required: [kind, name]
            discriminator: { propertyName: kind }
            properties:
              kind: { type: string }
              name: { type: string, example: Rex }
          Dog:
            allOf:
              - $ref: '#/components/schemas/Pet'
              - type: object
                properties:
                  barks: { type: boolean }
          Person:
            type: object
            properties:
              name: { type: string, minLength: 8 }
              manager: { $ref: '#/components/schemas/Person' }
        """;

    [Fact]
    public void Synthesizes_values_from_schemas()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document(Paths, Components, "3.1.0"));
        var examples = new OpenApiExamples(result.Document!);

        var shelf = examples.ForSchema(result.Document!.Components.Schemas["Shelf"]).ToJson();

        Assert.Equal(
            """{"pets":[{"kind":"Dog","name":"Rex","barks":true}],"owner":{"name":"stringxx"},"size":3,"code":"3fa85f64-5717-4562-b3fc-2c963f66afa6","state":"open"}""",
            shelf);
    }

    [Fact]
    public void Prefers_declared_examples()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document(Paths, Components, "3.1.0"));
        var media = result.Document!.Paths["/a"].Operations["get"].Responses["200"].Content["application/json"];
        var examples = new OpenApiExamples(result.Document);

        Assert.Equal("""{"pets":[]}""", examples.ForMediaType(media)!.ToJson());
        Assert.Equal("""{"pets":[]}""", examples.ForMediaType(media, "empty")!.ToJson());
        Assert.Equal("Rex", examples.ForSchema(result.Document.Components.Schemas["Pet"].Properties["name"]).ToString());
    }
}
