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
        var result = GeneratedCode.Generate(contract, new GeneratorOptions { Namespace = "Generated", GenerateQueries = true, GenerateMocks = true });

        if (Environment.GetEnvironmentVariable("BLAZORAPI_DUMP") is { Length: > 0 } dump)
        {
            GeneratedCode.Dump(result, Path.Combine(dump, contract.Replace('/', '_')));
        }

        GeneratedCode.Compile(result);
    }
}
