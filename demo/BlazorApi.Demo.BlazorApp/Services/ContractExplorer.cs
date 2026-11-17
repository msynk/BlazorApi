using System.Text.RegularExpressions;
using BlazorApi.CodeGeneration;
using BlazorApi.OpenApi.Model;

namespace Petstore.BlazorApp.Services;

/// <summary>A method of the generated client, read from the generated source.</summary>
public sealed record GeneratedMethod(string Name, string ReturnType, string Parameters, string? Summary, bool Obsolete, bool IsExtension)
{
    public string Signature => $"{ReturnType} {Name}({Parameters})";
}

/// <summary>
/// Runs the BlazorApi generator at runtime on the sample contract: the same engine (<c>ApiClientGenerator</c>) the source
/// generator runs during the build, with the same options, so the files it returns are the code this app compiles.
/// </summary>
public sealed partial class ContractExplorer(SampleSources sources)
{
    public const string ContractPath = "Contracts/petstore.yaml";
    public const string Namespace = "Petstore.BlazorApp.Api";

    private readonly Lock _lock = new();
    private GenerationResult? _default;

    /// <summary>The options the source generator uses for this app (<c>Namespace</c> from the OpenApiContract item).</summary>
    public static GeneratorOptions DefaultOptions { get; } = new() { Namespace = Namespace };

    public string ContractText => sources.Get(ContractPath);

    /// <summary>The generation result for the sample contract with the app's options.</summary>
    public GenerationResult Default
    {
        get
        {
            lock (_lock)
            {
                return _default ??= Generate(ContractText, DefaultOptions);
            }
        }
    }

    public ApiModel Model => Default.Model!;

    public static GenerationResult Generate(string contract, GeneratorOptions options) =>
        ApiClientGenerator.Generate(contract, "petstore.yaml", options);

    /// <summary>The C# method name the generator derives from an operation id (<c>listPets</c> becomes <c>ListPets</c>).</summary>
    public static string MethodBaseName(ApiOperation operation) =>
        operation.OperationId is { Length: > 0 } id ? char.ToUpperInvariant(id[0]) + id[1..] : operation.Method + operation.Path;

    /// <summary>The methods of the client interface and the pagination extensions, parsed from the generated files.</summary>
    public static IReadOnlyList<GeneratedMethod> Methods(GenerationResult result)
    {
        var methods = new List<GeneratedMethod>();
        foreach (var file in result.Files)
        {
            var inInterface = false;
            var docs = new List<string>();
            var obsolete = false;
            foreach (var raw in file.Content.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Contains(" interface I", StringComparison.Ordinal))
                {
                    inInterface = true;
                    continue;
                }

                var trimmed = line.Trim();
                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    docs.Add(trimmed[3..].Trim());
                    continue;
                }

                if (trimmed.StartsWith("[global::System.Obsolete", StringComparison.Ordinal))
                {
                    obsolete = true;
                    continue;
                }

                var extension = ExtensionPattern().Match(line);
                var member = inInterface ? InterfaceMemberPattern().Match(line) : Match.Empty;
                if (extension.Success || member.Success)
                {
                    var match = extension.Success ? extension : member;
                    methods.Add(new GeneratedMethod(
                        match.Groups["name"].Value,
                        Simplify(match.Groups["ret"].Value),
                        Simplify(match.Groups["params"].Value),
                        Summary(docs),
                        obsolete,
                        extension.Success));
                }

                if (trimmed.Length > 0 && !trimmed.StartsWith('['))
                {
                    docs.Clear();
                    obsolete = false;
                }

                if (inInterface && line.StartsWith('}'))
                {
                    inInterface = false;
                }
            }
        }

        return methods;
    }

    /// <summary>Drops <c>global::</c> namespace qualifiers so signatures read like hand-written code.</summary>
    public static string Simplify(string code) => QualifierPattern().Replace(code, string.Empty);

    private static string? Summary(List<string> docs)
    {
        var text = string.Join(' ', docs);
        var match = SummaryPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var summary = CrefPattern().Replace(match.Groups[1].Value, m => Simplify(m.Groups[1].Value));
        return TagPattern().Replace(summary, string.Empty).Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&").Trim();
    }

    [GeneratedRegex(@"^\s{4}(?<ret>[^\s(/\[][^(=]*?)\s+(?<name>\w+)\((?<params>.*)\);\s*$")]
    private static partial Regex InterfaceMemberPattern();

    [GeneratedRegex(@"^\s+public static (?:async )?(?<ret>[^(]+?)\s+(?<name>\w+)\((?<params>this .*)\)\s*$")]
    private static partial Regex ExtensionPattern();

    [GeneratedRegex(@"global::(?:[A-Za-z_]\w*\.)*")]
    private static partial Regex QualifierPattern();

    [GeneratedRegex("<summary>(.*?)</summary>")]
    private static partial Regex SummaryPattern();

    [GeneratedRegex("<see (?:c|)ref=\"(?:[A-Z]:)?([^\"]+)\"\\s*/>")]
    private static partial Regex CrefPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();
}
