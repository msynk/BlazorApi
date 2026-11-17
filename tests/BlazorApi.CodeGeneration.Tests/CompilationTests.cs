using BlazorApi.CodeGeneration.Tests.Infrastructure;

namespace BlazorApi.CodeGeneration.Tests;

public class CompilationTests
{
    [Theory]
    [InlineData("petstore.yaml")]
    [InlineData("features-3.1.json")]
    [InlineData("external/main.yaml")]
    public void Generated_code_compiles_without_warnings(string contract)
    {
        var result = GeneratedCode.Generate(contract, new GeneratorOptions { Namespace = "Generated", GenerateMocks = true });

        if (Environment.GetEnvironmentVariable("BLAZORAPI_DUMP") is { Length: > 0 } dump)
        {
            GeneratedCode.Dump(result, Path.Combine(dump, contract.Replace('/', '_')));
        }

        GeneratedCode.Compile(result);
    }

    [Fact]
    public void Durations_can_be_time_spans()
    {
        var result = GeneratedCode.GenerateYaml("""
            openapi: 3.1.0
            info: { title: Jobs, version: '1' }
            paths:
              /jobs:
                get:
                  operationId: listJobs
                  parameters:
                    - { name: olderThan, in: query, schema: { type: string, format: duration } }
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema:
                            type: object
                            required: [timeout]
                            properties:
                              timeout: { type: string, format: duration }
                              retries: { type: array, items: { type: string, format: duration } }
            """, new GeneratorOptions { Namespace = "Jobs", DurationType = DurationType.TimeSpan, GenerateMocks = true });

        var client = result.Files.Single(f => f.Path == "JobsClient.g.cs").Content;
        Assert.Contains("global::System.TimeSpan? olderThan = null", client, StringComparison.Ordinal);
        Assert.Contains("ApiFormat.Value(olderThanValue)", client, StringComparison.Ordinal);
        GeneratedCode.Compile(result);
    }

    [Fact]
    public void Json_context_compiles_for_every_contract()
    {
        foreach (var contract in new[] { "petstore.yaml", "features-3.1.json", "external/main.yaml" })
        {
            var result = GeneratedCode.Generate(contract, new GeneratorOptions { Namespace = "Generated", GenerateJsonContext = true });
            Assert.Contains(result.Files, f => f.Path.EndsWith("JsonContext.g.cs", StringComparison.Ordinal));
        }
    }
}
