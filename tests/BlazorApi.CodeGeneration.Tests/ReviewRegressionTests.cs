using BlazorApi.CodeGeneration.Tests.Infrastructure;

namespace BlazorApi.CodeGeneration.Tests;

/// <summary>Regressions for the issues found by the code reviews of the generator.</summary>
public class ReviewRegressionTests
{
    private const string Contract = """
        openapi: 3.1.0
        info: { title: Shop, version: '1' }
        servers: [{ url: /api }]
        paths:
          /files/{id}:
            servers: [{ url: 'https://files.example.com/v2' }]
            get:
              operationId: getFile
              parameters: [{ name: id, in: path, required: true, schema: { type: string } }]
              responses:
                '200':
                  description: ok
                  headers:
                    X-Tier: { schema: { $ref: '#/components/schemas/Tier' } }
                    x_tier: { schema: { $ref: '#/components/schemas/Tier' } }
                  content:
                    application/json: { schema: { $ref: '#/components/schemas/FileResponseDto' } }
        components:
          schemas:
            Tier: { type: string, enum: [gold, silver] }
            Level: { type: integer, enum: [1, 2, 3] }
            FileResponseDto:
              type: object
              properties:
                level: { $ref: '#/components/schemas/Level' }
        """;

    private static GenerationResult Generate(GeneratorOptions? options = null) =>
        GeneratedCode.GenerateYaml(Contract, options ?? new GeneratorOptions
        {
            Namespace = "Shop",
            UnknownEnumValues = UnknownEnumValueHandling.Unrecognized,
            GenerateJsonContext = true,
        });

    private static string File(GenerationResult result, string path) => result.Files.Single(f => f.Path == path).Content;

    [Fact]
    public void Absolute_operation_servers_drop_the_document_base_path()
    {
        var client = File(Generate(), "ShopClient.g.cs");

        Assert.Contains("\"/files/{id}\", \"getFile\"", client, StringComparison.Ordinal);
        Assert.Contains("ServerAddress = \"https://files.example.com/v2\"", client, StringComparison.Ordinal);
    }

    [Fact]
    public void Integer_enums_refuse_to_write_the_unrecognized_member()
    {
        var level = File(Generate(), "Models/Level.g.cs");

        Assert.Contains("value == Level.Unrecognized ? throw new global::System.ArgumentOutOfRangeException", level, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_classes_compile_with_names_that_collide_after_conversion()
    {
        var result = Generate(new GeneratorOptions { Namespace = "Shop" });

        Assert.Contains(result.Files, f => f.Path == "ShopClientResponseHeaders.g.cs");
        GeneratedCode.Compile(result);
    }

    [Fact]
    public void Json_context_keeps_user_types_named_like_runtime_types()
    {
        var context = File(Generate(), "ShopJsonContext.g.cs");

        Assert.Contains("typeof(FileResponseDto)", context, StringComparison.Ordinal);
        Assert.DoesNotContain("global::BlazorApi.Runtime.FileResponse)", context, StringComparison.Ordinal);
    }
}
