using System.Collections.Immutable;
using System.Text;
using BlazorApi.CodeGeneration;
using BlazorApi.CodeGeneration.Configuration;
using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.References;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace BlazorApi.SourceGenerator;

/// <summary>
/// Generates typed clients from OpenAPI contracts added as MSBuild <c>OpenApiContract</c> items (AdditionalFiles with
/// <c>BlazorApiKind=Contract</c> metadata). Each contract is generated independently and only when its text, its
/// options or a referenced document changes; C# edits never re-run generation.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class BlazorApiGenerator : IIncrementalGenerator
{
    /// <summary>The tracking name of the per-contract generation step (for incrementality tests).</summary>
    public const string TrackingName = "BlazorApiGenerate";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => InputFile.Create(pair.Left, pair.Right.GetOptions(pair.Left), cancellationToken))
            .Where(static file => file is not null)
            .Select(static (file, _) => file!);

        // Contracts are generated independently: a contract's pipeline depends on its own text and options plus the
        // shared reference and configuration files, never on other contracts.
        var contracts = inputs.Where(static file => file.Kind == "Contract");
        var documents = inputs
            .Where(static file => file.Kind is "Reference" or "Config")
            .Collect()
            .Select(static (files, _) => new EquatableArray<InputFile>(files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path))));
        var rootNamespace = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => provider.GlobalOptions.TryGetValue("build_property.RootNamespace", out var value) ? value : null);

        var outputs = contracts
            .Combine(documents)
            .Combine(rootNamespace)
            .Select(static (input, cancellationToken) => Generate(input.Left.Left, input.Left.Right, input.Right, cancellationToken))
            .WithTrackingName(TrackingName);

        context.RegisterSourceOutput(outputs, static (spc, output) => Emit(spc, output));
    }

    internal static GenerationOutput Generate(InputFile contract, EquatableArray<InputFile> documents, string? rootNamespace, CancellationToken cancellationToken)
    {
        var diagnostics = new DiagnosticBag();
        try
        {
            var configuration = new GenerationConfiguration
            {
                Options = new GeneratorOptions { Namespace = DefaultNamespace(contract.Path, rootNamespace) },
            };

            if (!string.IsNullOrEmpty(contract.Config))
            {
                var config = documents.FirstOrDefault(d => d.Kind == "Config" && SamePath(d.Path, contract.Config!));
                if (config is null)
                {
                    diagnostics.Report(CodeGenerationDiagnostics.InvalidConfiguration, new SourceLocation(contract.Path, 0, 0), null,
                        $"The configuration file '{contract.Config}' is not an AdditionalFile (add it with the Config metadata of OpenApiContract)");
                }
                else
                {
                    configuration = ReadConfiguration(config, contract.Path, diagnostics, configuration);
                }
            }

            var options = configuration.Options;
            if (!string.IsNullOrEmpty(contract.Namespace))
            {
                options = options with { Namespace = contract.Namespace! };
            }

            if (!string.IsNullOrEmpty(contract.ClientName))
            {
                options = options with { ClientName = contract.ClientName };
            }

            options = GeneratorOptionsReader.ApplyPairs(options, contract.Options, diagnostics);
            cancellationToken.ThrowIfCancellationRequested();

            var resolver = new InMemoryDocumentResolver();
            foreach (var document in documents.Where(d => d.Kind != "Config"))
            {
                resolver.Add(document.Path, document.Text);
            }

            var loadOptions = new OpenApiLoadOptions
            {
                DocumentResolver = resolver,
                ExternalReferences = configuration.ExternalReferences,
                SeverityOverrides = options.SeverityOverrides,
                ReferenceRoot = configuration.ReferenceRoot,
            };
            var result = ApiClientGenerator.Generate(contract.Text, contract.Path, options, loadOptions);
            diagnostics.AddRange(result.Diagnostics);
            var prefix = HintPrefix(contract.Path);
            var files = result.Files
                .Select(f => new GeneratedSource(prefix + "." + f.Path.Replace('/', '.'), f.Content))
                .ToImmutableArray();
            return new GenerationOutput(contract.Path, new EquatableArray<GeneratedSource>(files), Convert(diagnostics, contract.Path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new DiagnosticInfo("BAPI9001", RoslynSeverity.Error, $"BlazorApi failed to generate code for '{contract.Path}': {ex.GetType().Name}: {ex.Message}", contract.Path, 0, 0);
            return new GenerationOutput(contract.Path, default, new EquatableArray<DiagnosticInfo>(ImmutableArray.Create(failure)));
        }
    }

    /// <summary>
    /// Reads a configuration file. With a <c>contracts</c> list, the entry whose <c>contract</c> names this contract
    /// applies on top of the top-level settings. A relative <c>references.root</c> is resolved against the file.
    /// </summary>
    private static GenerationConfiguration ReadConfiguration(InputFile config, string contractPath, DiagnosticBag diagnostics, GenerationConfiguration defaults)
    {
        var configuration = GeneratorOptionsReader.ReadJson(config.Text, config.Path, diagnostics, defaults);
        var root = BlazorApi.OpenApi.Syntax.SyntaxParser.Parse(config.Text, config.Path, BlazorApi.OpenApi.Syntax.DocumentFormat.Json, new DiagnosticBag());
        if (root is BlazorApi.OpenApi.Syntax.ObjectNode obj && obj["contracts"] is BlazorApi.OpenApi.Syntax.ArrayNode entries)
        {
            var entry = entries.Items.OfType<BlazorApi.OpenApi.Syntax.ObjectNode>().FirstOrDefault(e =>
                e["contract"] is BlazorApi.OpenApi.Syntax.ScalarNode { IsNull: false } name && SamePath(contractPath, name.Text));
            if (entry is not null)
            {
                configuration = GeneratorOptionsReader.ReadJson(entry.ToJson(), config.Path, diagnostics, configuration);
            }
        }

        if (configuration.ReferenceRoot is { Length: > 0 } referenceRoot && !IsRooted(referenceRoot))
        {
            configuration = configuration with { ReferenceRoot = DirectoryOf(config.Path) + referenceRoot.Replace('/', SeparatorOf(config.Path)) };
        }

        return configuration;
    }

    private static void Emit(SourceProductionContext context, GenerationOutput output)
    {
        foreach (var file in output.Files)
        {
            context.AddSource(file.HintName, SourceText.From(file.Content, Encoding.UTF8));
        }

        foreach (var diagnostic in output.Diagnostics)
        {
            var descriptor = new Microsoft.CodeAnalysis.DiagnosticDescriptor(diagnostic.Id, diagnostic.Id, "{0}", "BlazorApi", diagnostic.Severity, isEnabledByDefault: true);
            var location = diagnostic.Line > 0 && diagnostic.FilePath is not null
                ? Location.Create(diagnostic.FilePath, default, new LinePositionSpan(
                    new LinePosition(diagnostic.Line - 1, Math.Max(diagnostic.Column - 1, 0)),
                    new LinePosition(diagnostic.Line - 1, Math.Max(diagnostic.Column - 1, 0))))
                : Location.None;
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, diagnostic.Message));
        }
    }

    private static EquatableArray<DiagnosticInfo> Convert(DiagnosticBag diagnostics, string contractPath)
    {
        var directory = DirectoryOf(contractPath);
        return new EquatableArray<DiagnosticInfo>(diagnostics.ToSortedList()
            .Select(d => new DiagnosticInfo(
                d.Id,
                d.Severity switch
                {
                    OpenApi.Diagnostics.DiagnosticSeverity.Error => RoslynSeverity.Error,
                    OpenApi.Diagnostics.DiagnosticSeverity.Warning => RoslynSeverity.Warning,
                    _ => RoslynSeverity.Info,
                },
                d.Pointer is null ? d.Message : d.Message + " [" + d.Pointer + "]",
                d.Location.Source is { } source ? (IsRooted(source) ? source : directory + source.Replace('/', SeparatorOf(contractPath))) : contractPath,
                d.Location.Line,
                d.Location.Column))
            .ToImmutableArray());
    }

    private static string DefaultNamespace(string contractPath, string? rootNamespace)
    {
        var file = FileNameWithoutExtension(contractPath);
        var name = Identifier.Pascal(file);
        return string.IsNullOrEmpty(rootNamespace) ? name : rootNamespace + "." + name;
    }

    private static string HintPrefix(string path)
    {
        var name = FileName(path);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }

    private static bool SamePath(string fullPath, string relative)
    {
        var normalizedFull = fullPath.Replace('\\', '/');
        var normalizedRelative = relative.Replace('\\', '/').TrimStart('.', '/');
        return normalizedFull.EndsWith("/" + normalizedRelative, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFull, normalizedRelative, StringComparison.OrdinalIgnoreCase);
    }

    private static string FileName(string path)
    {
        var slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return path.Substring(slash + 1);
    }

    private static string FileNameWithoutExtension(string path)
    {
        var name = FileName(path);
        var dot = name.IndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }

    private static string DirectoryOf(string path)
    {
        var slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return slash >= 0 ? path.Substring(0, slash + 1) : string.Empty;
    }

    private static char SeparatorOf(string path) => path.IndexOf('\\') >= 0 ? '\\' : '/';

    private static bool IsRooted(string path) =>
        path.StartsWith("/", StringComparison.Ordinal) || (path.Length > 2 && path[1] == ':') || path.StartsWith("\\\\", StringComparison.Ordinal);
}

/// <summary>An AdditionalFile relevant to generation, with its text and metadata (value-equatable).</summary>
internal sealed record InputFile(string Path, string Text, string Kind, string? Namespace, string? ClientName, string? Options, string? Config)
{
    public static InputFile? Create(AdditionalText file, AnalyzerConfigOptions options, CancellationToken cancellationToken)
    {
        string? Metadata(string name) =>
            options.TryGetValue("build_metadata.AdditionalFiles." + name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        var kind = Metadata("BlazorApiKind");
        if (kind is not ("Contract" or "Reference" or "Config"))
        {
            return null;
        }

        var text = file.GetText(cancellationToken)?.ToString();
        return text is null ? null : new InputFile(file.Path, text, kind, Metadata("BlazorApiNamespace"), Metadata("BlazorApiClientName"), Metadata("BlazorApiOptions"), Metadata("BlazorApiConfig"));
    }
}

/// <summary>A generated source file.</summary>
internal sealed record GeneratedSource(string HintName, string Content);

/// <summary>A diagnostic in a form that is cheap to compare and cache.</summary>
internal sealed record DiagnosticInfo(string Id, RoslynSeverity Severity, string Message, string? FilePath, int Line, int Column);

/// <summary>The generation output of one contract.</summary>
internal sealed record GenerationOutput(string ContractPath, EquatableArray<GeneratedSource> Files, EquatableArray<DiagnosticInfo> Diagnostics);
