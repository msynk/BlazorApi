namespace BlazorApi.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "blazorapi-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests()
    {
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Contracts"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_directory, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "Contracts"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (int Exit, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = new CliApp(output, error, _directory).Run(args);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void Generate_writes_files_and_a_manifest()
    {
        var (exit, output, _) = Run("generate", "petstore.yaml", "--output", "out", "--namespace", "Demo.Pets");

        Assert.Equal(0, exit);
        Assert.Contains("Generated", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_directory, "out", "PetstoreClient.g.cs")));
        Assert.Contains("namespace Demo.Pets;", File.ReadAllText(Path.Combine(_directory, "out", "Models", "Pet.g.cs")), StringComparison.Ordinal);
        Assert.Contains("Models/Pet.g.cs", File.ReadAllLines(Path.Combine(_directory, "out", ".blazorapi-manifest")));
    }

    [Fact]
    public void Regeneration_removes_stale_files_but_never_user_files()
    {
        Run("generate", "petstore.yaml", "--output", "out");
        File.WriteAllText(Path.Combine(_directory, "out", "Custom.cs"), "// mine");

        var (exit, _, _) = Run("generate", "petstore.yaml", "--output", "out", "--option", "IncludeTags=store", "--option", "IncludeUnusedModels=false");

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(_directory, "out", "Models", "Dog.g.cs")));
        Assert.True(File.Exists(Path.Combine(_directory, "out", "Models", "Order.g.cs")));
        Assert.True(File.Exists(Path.Combine(_directory, "out", "Custom.cs")));
    }

    [Fact]
    public void Check_fails_when_output_is_out_of_date_and_dry_run_writes_nothing()
    {
        var (dryExit, dryOutput, _) = Run("generate", "petstore.yaml", "--output", "out", "--dry-run");
        var (checkExit, _, checkError) = Run("generate", "petstore.yaml", "--output", "out", "--check");
        Run("generate", "petstore.yaml", "--output", "out");
        var (upToDateExit, upToDateOutput, _) = Run("generate", "petstore.yaml", "--output", "out", "--check");

        Assert.Equal(0, dryExit);
        Assert.Contains("create", dryOutput, StringComparison.Ordinal);
        Assert.Equal(1, checkExit);
        Assert.Contains("out of date", checkError, StringComparison.Ordinal);
        Assert.Equal(0, upToDateExit);
        Assert.Contains("up to date", upToDateOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_reads_a_configuration_with_several_contracts()
    {
        File.WriteAllText(Path.Combine(_directory, "blazorapi.json"), """
            {
              "contracts": [
                { "contract": "petstore.yaml", "output": "gen/pets", "namespace": "Pets", "generateQueries": true },
                { "contract": "features-3.1.json", "output": "gen/features", "namespace": "Features", "diagnostics": { "BAPI4002": "none" } }
              ]
            }
            """);

        var (exit, _, error) = Run("generate");

        Assert.True(exit == 0, error);
        Assert.True(File.Exists(Path.Combine(_directory, "gen", "pets", "PetstoreQueries.g.cs")));
        Assert.True(File.Exists(Path.Combine(_directory, "gen", "features", "Models", "Shape.g.cs")));
    }

    [Fact]
    public void Top_level_settings_are_defaults_for_listed_contracts()
    {
        File.WriteAllText(Path.Combine(_directory, "blazorapi.json"), """
            {
              "generateQueries": true,
              "contracts": [ { "contract": "petstore.yaml", "output": "gen", "namespace": "Pets" } ]
            }
            """);

        var (exit, _, error) = Run("generate");

        Assert.True(exit == 0, error);
        Assert.True(File.Exists(Path.Combine(_directory, "gen", "PetstoreQueries.g.cs")));
    }

    [Fact]
    public void Invalid_option_values_are_usage_errors()
    {
        Assert.Equal(2, Run("diff", "petstore.yaml", "petstore.yaml", "--fail-on", "sometimes").Exit);
        var (exit, _, error) = Run("validate", "petstore.yaml", "--option", "Grouping=sideways");
        Assert.Equal(2, exit);
        Assert.Contains("BAPI5001", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_problems_are_reported()
    {
        File.WriteAllText(Path.Combine(_directory, "bad.json"), """{ "contract": "petstore.yaml", "grouping": "sideways", "colour": "blue" }""");

        var (exit, _, error) = Run("generate", "--config", "bad.json");

        Assert.Equal(1, exit);
        Assert.Contains("BAPI5001", error, StringComparison.Ordinal);
        Assert.Contains("BAPI5002", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_reports_errors_with_exit_codes_and_json()
    {
        File.WriteAllText(Path.Combine(_directory, "broken.yaml"), "openapi: 3.0.3\ninfo: {title: T}\npaths: {}\n");

        var (okExit, okOutput, _) = Run("validate", "petstore.yaml");
        var (badExit, badJson, _) = Run("validate", "broken.yaml", "--format", "json");

        Assert.Equal(0, okExit);
        Assert.Contains("0 error(s)", okOutput, StringComparison.Ordinal);
        Assert.Equal(1, badExit);
        Assert.Contains("\"id\":\"BAPI1007\"", badJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_mode_fails_on_warnings()
    {
        File.WriteAllText(Path.Combine(_directory, "warn.yaml"), "openapi: 3.0.3\ninfo: {title: T, version: '1'}\npaths: {}\nx-ok: 1\nunknownField: 1\n");

        var (lenient, _, _) = Run("validate", "warn.yaml");
        var (strict, _, error) = Run("validate", "warn.yaml", "--strict");

        Assert.Equal(0, lenient);
        Assert.Equal(1, strict);
        Assert.Contains("error BAPI1008", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_fails_on_breaking_changes_by_default()
    {
        var petstore = File.ReadAllText(Path.Combine(_directory, "petstore.yaml"));
        File.WriteAllText(Path.Combine(_directory, "v2.yaml"), petstore.Replace("  /health:\n", "  /healthz:\n").Replace("  /health:\r\n", "  /healthz:\r\n"));

        var (breaking, output, _) = Run("diff", "petstore.yaml", "v2.yaml");
        var (ignored, _, _) = Run("diff", "petstore.yaml", "v2.yaml", "--fail-on", "none");
        var (json, jsonOutput, _) = Run("diff", "petstore.yaml", "v2.yaml", "--format", "json", "--report", "report.json");

        Assert.Equal(1, breaking);
        Assert.Contains("operation-moved", output, StringComparison.Ordinal);
        Assert.Equal(0, ignored);
        Assert.Equal(1, json);
        Assert.StartsWith("{\"maxSeverity\":\"breaking\"", jsonOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_directory, "report.json")));
    }

    [Fact]
    public void Inspect_summarizes_the_contract()
    {
        var (exit, output, _) = Run("inspect", "petstore.yaml");
        var (jsonExit, json, _) = Run("inspect", "petstore.yaml", "--format", "json");

        Assert.Equal(0, exit);
        Assert.Contains("paged:cursor", output, StringComparison.Ordinal);
        Assert.Contains("discriminator 'kind'", output, StringComparison.Ordinal);
        Assert.Equal(0, jsonExit);
        Assert.Contains("\"operationId\":\"listPets\",\"pagination\":\"cursor\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], 2)]
    [InlineData(new[] { "frobnicate" }, 2)]
    [InlineData(new[] { "validate" }, 2)]
    [InlineData(new[] { "--help" }, 0)]
    [InlineData(new[] { "--version" }, 0)]
    public void Usage_errors_and_help(string[] args, int expected)
    {
        Assert.Equal(expected, Run(args).Exit);
    }
}
