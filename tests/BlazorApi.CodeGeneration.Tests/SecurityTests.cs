using BlazorApi.CodeGeneration.Tests.Infrastructure;

namespace BlazorApi.CodeGeneration.Tests;

/// <summary>Contracts are untrusted input: nothing in them may change the meaning of generated code.</summary>
public class SecurityTests
{
    [Fact]
    public void Hostile_names_descriptions_and_values_cannot_inject_code()
    {
        var lineSeparator = ((char)0x2028).ToString();
        var nextLine = ((char)0x85).ToString();
        var injection = "x" + lineSeparator + "public class Injected1 { }" + nextLine + "public class Injected2 { }";
        var yaml = string.Join("\n",
            "openapi: 3.0.3",
            "info:",
            "  title: \"Evil\\n*/ public class Injected3 { } //\"",
            "  version: \"1\\\" ; class Injected4 {}\"",
            "paths:",
            "  '/items\"); class Injected5 //':",
            "    get:",
            "      operationId: \"get\\\"); class Injected6 {} //\"",
            "      summary: \"" + injection + "\"",
            "      parameters:",
            "        - name: id\"); class Injected7 { } //",
            "          in: query",
            "          schema: {type: string, pattern: \"\\\"); class Injected8 {} //\"}",
            "      responses:",
            "        '200':",
            "          description: \"" + injection + "\"",
            "          content:",
            "            application/json:",
            "              schema: {$ref: '#/components/schemas/Thing\"; class Injected9 {}'}",
            "components:",
            "  schemas:",
            "    'Thing\"; class Injected9 {}':",
            "      type: object",
            "      description: \"</summary><script/> " + injection + "\"",
            "      properties:",
            "        'class':",
            "          type: string",
            "          enum: ['a\"b', 'c\\\\d', \"" + injection + "\"]",
            "        '1st value':",
            "          type: integer",
            "          default: 1",
            "          enum: [1, 1e2]");

        var result = GeneratedCode.GenerateYaml(yaml, new GeneratorOptions { Namespace = "Hostile", GenerateQueries = true, GenerateMocks = true });
        var assembly = GeneratedCode.Compile(result);

        Assert.DoesNotContain(assembly.GetTypes(), t => t.Name.StartsWith("Injected", StringComparison.Ordinal));
        var allCode = string.Concat(result.Files.Select(f => f.Content));
        Assert.DoesNotContain(lineSeparator, allCode, StringComparison.Ordinal);
        Assert.DoesNotContain(nextLine, allCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bad Namespace")]
    [InlineData("class")]
    [InlineData("Ns;System")]
    public void Invalid_namespaces_are_rejected(string ns)
    {
        var result = ApiClientGenerator.Generate("openapi: 3.0.3\ninfo: {title: T, version: '1'}\npaths: {}", "x.yaml", new GeneratorOptions { Namespace = ns });

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Id == "BAPI5001");
        Assert.Empty(result.Files);
    }
}
