using BlazorApi.CodeGeneration.Tests.Infrastructure;

namespace BlazorApi.CodeGeneration.Tests;

/// <summary>
/// Compares generated code with the checked-in expected output under <c>tests/Golden</c>. The integration tests
/// compile that output, so a golden update is also exercised end to end. To accept intended changes, run the tests
/// with the environment variable <c>BLAZORAPI_UPDATE_GOLDEN=1</c> and review the diff.
/// </summary>
public class GoldenTests
{
    public static TheoryData<string> Cases => new() { "petstore", "features" };

    internal static GeneratorOptions OptionsFor(string name) => name switch
    {
        "petstore" => new GeneratorOptions { Namespace = "Petstore.Client", GenerateQueries = true, GenerateMocks = true },
        "features" => new GeneratorOptions { Namespace = "Features.Client" },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    internal static string ContractFor(string name) => name switch
    {
        "petstore" => "petstore.yaml",
        "features" => "features-3.1.json",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Generated_code_matches_golden_files(string name)
    {
        var result = GeneratedCode.Generate(ContractFor(name), OptionsFor(name));
        var directory = Path.Combine(RepositoryRoot(), "tests", "Golden", name);
        var update = Environment.GetEnvironmentVariable("BLAZORAPI_UPDATE_GOLDEN") == "1";
        if (update)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            GeneratedCode.Dump(result, directory);
            return;
        }

        var expected = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(directory, f).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList()
            : [];
        Assert.Equal(expected, result.Files.Select(f => f.Path).OrderBy(f => f, StringComparer.Ordinal).ToList());

        var mismatches = new List<string>();
        foreach (var file in result.Files)
        {
            var path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var actual = file.Content;
            var golden = File.ReadAllText(path).Replace("\r\n", "\n");
            if (!string.Equals(golden, actual, StringComparison.Ordinal))
            {
                File.WriteAllText(Path.ChangeExtension(path, ".received.cs"), actual);
                mismatches.Add(file.Path);
            }
        }

        Assert.True(mismatches.Count == 0, "Generated code differs from the golden files (see *.received.cs): " + string.Join(", ", mismatches));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Generation_is_deterministic(string name)
    {
        var first = GeneratedCode.Generate(ContractFor(name), OptionsFor(name));
        var second = GeneratedCode.Generate(ContractFor(name), OptionsFor(name));

        Assert.Equal(first.Files.Select(f => f.Path + "\n" + f.Content), second.Files.Select(f => f.Path + "\n" + f.Content));
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlazorApi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (BlazorApi.slnx) was not found.");
    }
}
