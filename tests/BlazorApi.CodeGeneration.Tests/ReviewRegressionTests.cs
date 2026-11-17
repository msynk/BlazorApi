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
    public void Type_mappings_replace_generated_types()
    {
        const string contract = """
            openapi: 3.1.0
            info: { title: Bank, version: '1' }
            paths:
              /accounts/{id}:
                get:
                  operationId: getAccount
                  parameters:
                    - { name: id, in: path, required: true, schema: { type: string } }
                    - { name: minimum, in: query, schema: { type: string, format: decimal-string } }
                    - { name: currency, in: query, schema: { $ref: '#/components/schemas/Money' } }
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json: { schema: { $ref: '#/components/schemas/Account' } }
                put:
                  operationId: putBalance
                  parameters: [{ name: id, in: path, required: true, schema: { type: string } }]
                  requestBody:
                    required: true
                    content:
                      application/json: { schema: { $ref: '#/components/schemas/Money' } }
                  responses: { '204': { description: ok } }
            components:
              schemas:
                Money:
                  type: object
                  required: [amount, currency]
                  properties:
                    amount: { type: string, format: decimal-string }
                    currency: { type: string, minLength: 3, maxLength: 3 }
                Account:
                  type: object
                  required: [balance]
                  properties:
                    balance: { $ref: '#/components/schemas/Money' }
                    history: { type: array, items: { $ref: '#/components/schemas/Money' } }
                    limit: { type: string, format: decimal-string, maxLength: 20 }
            """;
        const string shared = """
            namespace Shared.Finance;
            public sealed record Money(decimal Amount, string Currency);
            """;
        var result = GeneratedCode.GenerateYaml(contract, new GeneratorOptions
        {
            Namespace = "Bank",
            GenerateMocks = true,
            GenerateJsonContext = true,
            TypeMappings = new Dictionary<string, string> { ["Money"] = "Shared.Finance.Money", ["format:decimal-string"] = "decimal" },
        });

        Assert.DoesNotContain(result.Files, f => f.Path == "Models/Money.g.cs");
        var account = File(result, "Models/Account.g.cs");
        Assert.Contains("public required global::Shared.Finance.Money Balance", account, StringComparison.Ordinal);
        Assert.Contains("public decimal? Limit", account, StringComparison.Ordinal);
        Assert.DoesNotContain("StringLength", account, StringComparison.Ordinal);
        Assert.Contains("typeof(global::Shared.Finance.Money)", File(result, "BankJsonContext.g.cs"), StringComparison.Ordinal);
        // The System.Text.Json generator does not run in this test compilation, so compile without the context.
        GeneratedCode.Compile(GeneratedCode.GenerateYaml(contract, new GeneratorOptions
        {
            Namespace = "Bank",
            GenerateMocks = true,
            TypeMappings = new Dictionary<string, string> { ["Money"] = "Shared.Finance.Money", ["format:decimal-string"] = "decimal" },
        }), shared);
    }

    [Fact]
    public void Type_mappings_are_read_from_key_value_pairs()
    {
        var bag = new BlazorApi.OpenApi.Diagnostics.DiagnosticBag();
        var options = BlazorApi.CodeGeneration.Configuration.GeneratorOptionsReader.ApplyPairs(new GeneratorOptions(), "typeMappings=Money->Shared.Money,format:money->decimal", bag);

        Assert.Empty(bag);
        Assert.Equal("Shared.Money", options.TypeMappings["Money"]);
        Assert.Equal("decimal", options.TypeMappings["format:money"]);
    }

    [Fact]
    public void Json_context_keeps_user_types_named_like_runtime_types()
    {
        var context = File(Generate(), "ShopJsonContext.g.cs");

        Assert.Contains("typeof(FileResponseDto)", context, StringComparison.Ordinal);
        Assert.DoesNotContain("global::BlazorApi.Runtime.FileResponse)", context, StringComparison.Ordinal);
    }
}
