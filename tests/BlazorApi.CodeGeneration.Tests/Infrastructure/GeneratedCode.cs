using System.Reflection;
using System.Runtime.Loader;
using BlazorApi.CodeGeneration;
using BlazorApi.CodeGeneration.CSharp;
using BlazorApi.OpenApi;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BlazorApi.CodeGeneration.Tests.Infrastructure;

/// <summary>Generates code from contracts and compiles it in memory, the way a consuming project would.</summary>
internal static class GeneratedCode
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    public static string ContractPath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Contracts", relative.Replace('/', Path.DirectorySeparatorChar));

    public static GenerationResult Generate(string contract, GeneratorOptions? options = null)
    {
        var path = ContractPath(contract);
        var result = ApiClientGenerator.Generate(OpenApiLoader.LoadFile(path), options ?? new GeneratorOptions { Namespace = "Generated" });
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        return result;
    }

    public static GenerationResult GenerateYaml(string yaml, GeneratorOptions? options = null)
    {
        var result = ApiClientGenerator.Generate(yaml.Replace("\r\n", "\n"), "inline.yaml", options ?? new GeneratorOptions { Namespace = "Generated" });
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        return result;
    }

    /// <summary>Compiles generated files (plus optional extra sources) and fails on any error or warning.</summary>
    public static Assembly Compile(GenerationResult result, params string[] extraSources)
    {
        var (assembly, diagnostics) = TryCompile(result.Files, extraSources);
        Assert.True(assembly is not null && diagnostics.Count == 0, "Generated code does not compile cleanly:\n" + string.Join("\n", diagnostics.Take(50)));
        return assembly!;
    }

    public static (Assembly? Assembly, IReadOnlyList<string> Diagnostics) TryCompile(IEnumerable<GeneratedFile> files, params string[] extraSources)
    {
        var parse = new CSharpParseOptions(LanguageVersion.CSharp12);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Content, parse, path: f.Path))
            .Concat(extraSources.Select((s, i) => CSharpSyntaxTree.ParseText(s, parse, path: $"Extra{i}.cs")))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "Generated_" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, warningLevel: 7));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        var problems = emit.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Id != "CS1701" && d.Id != "CS1702")
            .Select(d => d.ToString())
            .ToList();
        if (!emit.Success)
        {
            return (null, problems);
        }

        stream.Position = 0;
        var context = new AssemblyLoadContext("generated-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        return (context.LoadFromStream(stream), problems);
    }

    /// <summary>Writes generated files to a directory (for manual inspection while developing).</summary>
    public static void Dump(GenerationResult result, string directory)
    {
        foreach (var file in result.Files)
        {
            var path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
        }
    }
}
