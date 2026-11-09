using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace BlazorApi.SourceGenerator.Tests;

public class GeneratorTests
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    private static string Contract(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Contracts", name.Replace('/', Path.DirectorySeparatorChar));

    private static CSharpCompilation Compilation(params string[] sources) =>
        CSharpCompilation.Create(
            "Consumer",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.CSharp12))),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static GeneratorDriver Driver(params TestAdditionalFile[] files) =>
        CSharpGeneratorDriver.Create(
            [new BlazorApiGenerator().AsSourceGenerator()],
            files,
            new CSharpParseOptions(LanguageVersion.CSharp12),
            new TestOptionsProvider(files, new Dictionary<string, string> { ["build_property.RootNamespace"] = "Consumer" }),
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    private static TestAdditionalFile ContractFile(string name, Dictionary<string, string>? metadata = null)
    {
        var values = new Dictionary<string, string> { ["BlazorApiKind"] = "Contract" };
        foreach (var pair in metadata ?? [])
        {
            values[pair.Key] = pair.Value;
        }

        var path = Contract(name);
        return new TestAdditionalFile(path, File.ReadAllText(path), values);
    }

    [Fact]
    public void Generates_compilable_sources_for_a_contract()
    {
        var compilation = Compilation("namespace Consumer; public static class Usage { public static System.Type T => typeof(Consumer.Petstore.PetstoreClient); }");
        var driver = Driver(ContractFile("petstore.yaml", new() { ["BlazorApiOptions"] = "GenerateQueries=true" }));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        Assert.Empty(generatorDiagnostics);
        var errors = output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(20)));
        var hints = driver.GetRunResult().Results.Single().GeneratedSources.Select(s => s.HintName).ToList();
        Assert.Contains("petstore_yaml.Models.Pet.g.cs", hints);
        Assert.Contains("petstore_yaml.PetstoreClient.g.cs", hints);
        Assert.Contains("petstore_yaml.PetstoreQueries.g.cs", hints);
    }

    [Fact]
    public void Namespace_and_client_name_metadata_are_applied()
    {
        var driver = Driver(ContractFile("petstore.yaml", new() { ["BlazorApiNamespace"] = "My.Pets", ["BlazorApiClientName"] = "PetsApi" }));

        driver = driver.RunGenerators(Compilation("class C { }"));

        var client = driver.GetRunResult().Results.Single().GeneratedSources.Single(s => s.HintName == "petstore_yaml.PetsApi.g.cs");
        Assert.Contains("namespace My.Pets;", client.SourceText.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_problems_become_compiler_diagnostics_with_locations()
    {
        var file = new TestAdditionalFile(Path.Combine(Path.GetTempPath(), "broken.yaml"), "openapi: 3.0.3\ninfo: {title: T, version: '1'}\npaths:\n  /a:\n    get:\n      responses: {'200': {description: ok}}\n      bogus: true\n  /b: [unclosed",
            new Dictionary<string, string> { ["BlazorApiKind"] = "Contract" });

        var driver = Driver(file).RunGenerators(Compilation("class C { }"));
        var diagnostic = Assert.Single(driver.GetRunResult().Diagnostics);

        Assert.Equal("BAPI1001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(7, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.EndsWith("broken.yaml", diagnostic.Location.GetLineSpan().Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolves_external_references_from_reference_files()
    {
        var main = ContractFile("external/main.yaml");
        var common = new TestAdditionalFile(Contract("external/schemas/common.yaml"), File.ReadAllText(Contract("external/schemas/common.yaml")), new Dictionary<string, string> { ["BlazorApiKind"] = "Reference" });
        var item = new TestAdditionalFile(Contract("external/schemas/item.yaml"), File.ReadAllText(Contract("external/schemas/item.yaml")), new Dictionary<string, string> { ["BlazorApiKind"] = "Reference" });

        var driver = Driver(main, common, item).RunGeneratorsAndUpdateCompilation(Compilation("class C { }"), out var output, out _);

        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains(driver.GetRunResult().Results.Single().GeneratedSources, s => s.HintName == "main_yaml.Models.Error.g.cs");
    }

    [Fact]
    public void Configuration_files_select_their_contract_entry_and_resolve_the_reference_root()
    {
        var configPath = Path.Combine(Path.GetDirectoryName(Contract("external/main.yaml"))!, "blazorapi.json");
        var config = new TestAdditionalFile(configPath, """
            {
              "references": { "root": "." },
              "contracts": [
                { "contract": "other.yaml", "namespace": "Wrong" },
                { "contract": "main.yaml", "namespace": "From.Config", "generateQueries": true }
              ]
            }
            """, new Dictionary<string, string> { ["BlazorApiKind"] = "Config" });
        var main = ContractFile("external/main.yaml", new() { ["BlazorApiConfig"] = "blazorapi.json" });
        var common = new TestAdditionalFile(Contract("external/schemas/common.yaml"), File.ReadAllText(Contract("external/schemas/common.yaml")), new Dictionary<string, string> { ["BlazorApiKind"] = "Reference" });
        var item = new TestAdditionalFile(Contract("external/schemas/item.yaml"), File.ReadAllText(Contract("external/schemas/item.yaml")), new Dictionary<string, string> { ["BlazorApiKind"] = "Reference" });

        var driver = Driver(main, common, item, config).RunGenerators(Compilation("class C { }"));

        Assert.Empty(driver.GetRunResult().Diagnostics);
        var sources = driver.GetRunResult().Results.Single().GeneratedSources;
        Assert.Contains("namespace From.Config;", sources.Single(s => s.HintName == "main_yaml.Models.Error.g.cs").SourceText.ToString(), StringComparison.Ordinal);
        Assert.Contains(sources, s => s.HintName == "main_yaml.ExternalReferencesQueries.g.cs");
    }

    [Fact]
    public void Missing_reference_files_are_reported()
    {
        var driver = Driver(ContractFile("external/main.yaml")).RunGenerators(Compilation("class C { }"));

        Assert.Contains(driver.GetRunResult().Diagnostics, d => d.Id == "BAPI1010");
    }

    [Fact]
    public void Unrelated_source_changes_do_not_regenerate()
    {
        var compilation = Compilation("class C { }");
        var driver = Driver(ContractFile("petstore.yaml")).RunGenerators(compilation);

        var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class D { }", new CSharpParseOptions(LanguageVersion.CSharp12)));
        driver = driver.RunGenerators(edited);

        var steps = driver.GetRunResult().Results.Single().TrackedSteps[BlazorApiGenerator.TrackingName];
        Assert.All(steps.SelectMany(s => s.Outputs), output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }

    [Fact]
    public void Contract_changes_regenerate_only_that_contract()
    {
        var petstore = ContractFile("petstore.yaml");
        var features = ContractFile("features-3.1.json");
        var compilation = Compilation("class C { }");
        var driver = Driver(petstore, features).RunGenerators(compilation);

        var changed = new TestAdditionalFile(features.Path, features.Text.Replace("Features 3.1", "Features 3.1 v2"), features.Metadata);
        driver = driver.ReplaceAdditionalText(features, changed).RunGenerators(compilation);

        var outputs = driver.GetRunResult().Results.Single().TrackedSteps[BlazorApiGenerator.TrackingName].SelectMany(s => s.Outputs).Select(o => o.Reason).ToList();
        Assert.Contains(IncrementalStepRunReason.Modified, outputs);
        Assert.Contains(IncrementalStepRunReason.Cached, outputs);
    }

    [Fact]
    public void Files_without_contract_metadata_are_ignored()
    {
        var plain = new TestAdditionalFile(Contract("petstore.yaml"), File.ReadAllText(Contract("petstore.yaml")), new Dictionary<string, string>());

        var driver = Driver(plain).RunGenerators(Compilation("class C { }"));

        Assert.Empty(driver.GetRunResult().GeneratedTrees);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        string Run() => string.Join("\n", Driver(ContractFile("petstore.yaml")).RunGenerators(Compilation("class C { }"))
            .GetRunResult().Results.Single().GeneratedSources.Select(s => s.HintName + s.SourceText));

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Invalid_options_are_reported()
    {
        var driver = Driver(ContractFile("petstore.yaml", new() { ["BlazorApiOptions"] = "Grouping=bogus;Unknown=1" })).RunGenerators(Compilation("class C { }"));

        var ids = driver.GetRunResult().Diagnostics.Select(d => d.Id).OrderBy(i => i).ToList();
        Assert.Equal(["BAPI5001", "BAPI5002"], ids);
    }
}

internal sealed class TestAdditionalFile : AdditionalText
{
    public TestAdditionalFile(string path, string text, IReadOnlyDictionary<string, string> metadata)
    {
        Path = path;
        Text = text;
        Metadata = metadata;
    }

    public override string Path { get; }

    public string Text { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(Text, Encoding.UTF8);
}

internal sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly Dictionary<string, TestOptions> _files;

    public TestOptionsProvider(IEnumerable<TestAdditionalFile> files, IReadOnlyDictionary<string, string> global)
    {
        _files = files.ToDictionary(f => f.Path, f => new TestOptions(f.Metadata.ToDictionary(p => "build_metadata.AdditionalFiles." + p.Key, p => p.Value)));
        GlobalOptions = new TestOptions(global.ToDictionary(p => p.Key, p => p.Value));
    }

    public override AnalyzerConfigOptions GlobalOptions { get; }

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new TestOptions([]);

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        _files.TryGetValue(textFile.Path, out var options) ? options : textFile is TestAdditionalFile test
            ? new TestOptions(test.Metadata.ToDictionary(p => "build_metadata.AdditionalFiles." + p.Key, p => p.Value))
            : new TestOptions([]);
}

internal sealed class TestOptions : AnalyzerConfigOptions
{
    private readonly Dictionary<string, string> _values;

    public TestOptions(Dictionary<string, string> values) => _values = values;

    public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) =>
        _values.TryGetValue(key, out value);
}
