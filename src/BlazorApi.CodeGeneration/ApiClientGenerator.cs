using System.Text.RegularExpressions;
using BlazorApi.CodeGeneration.CSharp;
using BlazorApi.OpenApi;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.CodeGeneration;

/// <summary>The output of code generation.</summary>
public sealed class GenerationResult
{
    internal GenerationResult(IReadOnlyList<GeneratedFile> files, IReadOnlyList<ApiDiagnostic> diagnostics, ApiModel? model)
    {
        Files = files;
        Diagnostics = diagnostics;
        Model = model;
    }

    /// <summary>The generated files, in a deterministic order. Empty when loading failed with errors.</summary>
    public IReadOnlyList<GeneratedFile> Files { get; }

    /// <summary>Every diagnostic of loading, validation, model building and generation, sorted by location.</summary>
    public IReadOnlyList<ApiDiagnostic> Diagnostics { get; }

    /// <summary>The API model, when the document could be loaded.</summary>
    public ApiModel? Model { get; }

    /// <summary>Whether any diagnostic is an error.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.IsError);
}

/// <summary>Generates C# clients and models from OpenAPI documents. The same entry point serves the CLI and the source generator.</summary>
public static class ApiClientGenerator
{
    private static readonly Regex NamespacePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant);

    /// <summary>Loads a document from text and generates code.</summary>
    public static GenerationResult Generate(string text, string? documentPath, GeneratorOptions options, OpenApiLoadOptions? loadOptions = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        loadOptions ??= OpenApiLoadOptions.Default;
        loadOptions = loadOptions with { SeverityOverrides = Merge(loadOptions.SeverityOverrides, options.SeverityOverrides) };
        return Generate(OpenApiLoader.Parse(text, documentPath, loadOptions), options);
    }

    /// <summary>Generates code from a loaded document. Nothing is generated when loading reported errors.</summary>
    public static GenerationResult Generate(OpenApiLoadResult loaded, GeneratorOptions options)
    {
        if (loaded is null)
        {
            throw new ArgumentNullException(nameof(loaded));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var diagnostics = new DiagnosticBag(options.SeverityOverrides);
        diagnostics.AddRange(loaded.Diagnostics);
        ValidateOptions(options, diagnostics);
        if (loaded.Document is null || diagnostics.HasErrors)
        {
            return new GenerationResult([], Finish(diagnostics, options), null);
        }

        var model = ApiModelBuilder.Build(loaded.Document, diagnostics);
        var plan = GenerationPlanner.Create(model, options, diagnostics);
        var files = new List<GeneratedFile>();
        files.AddRange(new ModelEmitter(plan).Emit());
        var clients = new ClientEmitter(plan);
        files.AddRange(clients.Emit());
        var pagination = new PaginationEmitter(plan, clients);
        foreach (var client in plan.Clients)
        {
            if (pagination.Emit(client) is { } pages)
            {
                files.Add(pages);
            }

            if (options.GenerateQueries)
            {
                files.Add(new QueryEmitter(plan, clients, pagination).Emit(client));
            }
        }

        if (options.GenerateMocks)
        {
            var mocks = new MockEmitter(plan, clients);
            files.AddRange(plan.Clients.Select(mocks.EmitMock));
            if (mocks.EmitFixtures() is { } fixtures)
            {
                files.Add(fixtures);
            }
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new GenerationResult(files, Finish(diagnostics, options), model);
    }

    private static IReadOnlyList<ApiDiagnostic> Finish(DiagnosticBag diagnostics, GeneratorOptions options)
    {
        var sorted = diagnostics.ToSortedList();
        return options.TreatWarningsAsErrors
            ? sorted.Select(d => d.Severity == DiagnosticSeverity.Warning ? new ApiDiagnostic(d.Id, DiagnosticSeverity.Error, d.Message, d.Location, d.Pointer) : d).ToArray()
            : sorted;
    }

    private static void ValidateOptions(GeneratorOptions options, DiagnosticBag diagnostics)
    {
        void Check(string option, string? value)
        {
            if (value is not null && (!NamespacePattern.IsMatch(value) || value.Split('.').Any(Naming.Identifier.IsKeyword)))
            {
                diagnostics.Report(CodeGenerationDiagnostics.InvalidOption, SourceLocation.None, null, option, value, "it is not a valid C# namespace");
            }
        }

        Check("namespace", options.Namespace);
        Check("modelsNamespace", options.ModelsNamespace);
        if (options.ClientName is { } clientName && !Regex.IsMatch(clientName, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            diagnostics.Report(CodeGenerationDiagnostics.InvalidOption, SourceLocation.None, null, "clientName", clientName, "it is not a valid C# identifier");
        }
    }

    private static IReadOnlyDictionary<string, DiagnosticSeverity?>? Merge(
        IReadOnlyDictionary<string, DiagnosticSeverity?>? first,
        IReadOnlyDictionary<string, DiagnosticSeverity?> second)
    {
        if (first is null || first.Count == 0)
        {
            return second;
        }

        var merged = first.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var pair in second)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }
}
