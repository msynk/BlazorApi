using System.Globalization;
using BlazorApi.OpenApi;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.CodeGeneration.Configuration;

/// <summary>A generation configuration: what to generate from which contract, and how.</summary>
public sealed record GenerationConfiguration
{
    /// <summary>The contract path (relative to the configuration file), when the configuration names one.</summary>
    public string? Contract { get; init; }

    /// <summary>The output directory (relative to the configuration file), when the configuration names one.</summary>
    public string? Output { get; init; }

    /// <summary>The code generation options.</summary>
    public GeneratorOptions Options { get; init; } = new();

    /// <summary>Which external references may be followed.</summary>
    public ExternalReferencePolicy ExternalReferences { get; init; } = ExternalReferencePolicy.LocalFiles;

    /// <summary>The directory referenced files must be inside, relative to the configuration file.</summary>
    public string? ReferenceRoot { get; init; }
}

/// <summary>
/// Reads generation options from a JSON configuration file (<c>blazorapi.json</c>) or from <c>key=value</c> pairs
/// (MSBuild item metadata and command-line options). Keys are case-insensitive and use the
/// <see cref="GeneratorOptions"/> property names in camelCase.
/// </summary>
public static class GeneratorOptionsReader
{
    /// <summary>Reads a JSON configuration. Problems are reported as BAPI5xxx diagnostics.</summary>
    public static GenerationConfiguration ReadJson(string json, string? source, DiagnosticBag diagnostics, GenerationConfiguration? defaults = null)
    {
        var configuration = defaults ?? new GenerationConfiguration();
        var node = SyntaxParser.Parse(json, source ?? "blazorapi.json", DocumentFormat.Json, diagnostics);
        if (node is null)
        {
            return configuration;
        }

        if (node is not ObjectNode root)
        {
            diagnostics.Report(CodeGenerationDiagnostics.InvalidConfiguration, node.Location, null, "The configuration must be a JSON object");
            return configuration;
        }

        var options = configuration.Options;
        foreach (var property in root.Properties)
        {
            var location = property.KeyLocation;
            switch (property.Name.ToLowerInvariant())
            {
                case "$schema":
                case "contracts":
                    // A list of per-contract configurations, handled by the CLI and the source generator; the other
                    // top-level keys act as defaults for every entry.
                    break;
                case "contract":
                    configuration = configuration with { Contract = Text(property, diagnostics) };
                    break;
                case "output":
                    configuration = configuration with { Output = Text(property, diagnostics) };
                    break;
                case "references":
                    if (property.Value is ObjectNode references)
                    {
                        foreach (var reference in references.Properties)
                        {
                            switch (reference.Name.ToLowerInvariant())
                            {
                                case "policy":
                                    configuration = configuration with { ExternalReferences = ParseEnum(reference, ExternalReferencePolicy.LocalFiles, diagnostics) };
                                    break;
                                case "root":
                                    configuration = configuration with { ReferenceRoot = Text(reference, diagnostics) };
                                    break;
                                default:
                                    diagnostics.Report(CodeGenerationDiagnostics.UnknownOption, reference.KeyLocation, null, "references." + reference.Name);
                                    break;
                            }
                        }
                    }
                    else
                    {
                        Invalid(property, diagnostics, "expected an object with 'policy' and 'root'");
                    }

                    break;
                case "include":
                case "exclude":
                    if (property.Value is ObjectNode filter)
                    {
                        var include = property.Name.Equals("include", StringComparison.OrdinalIgnoreCase);
                        foreach (var entry in filter.Properties)
                        {
                            var values = List(entry, diagnostics);
                            switch (entry.Name.ToLowerInvariant())
                            {
                                case "operations":
                                    options = include ? options with { IncludeOperations = values } : options with { ExcludeOperations = values };
                                    break;
                                case "tags":
                                    options = include ? options with { IncludeTags = values } : options with { ExcludeTags = values };
                                    break;
                                default:
                                    diagnostics.Report(CodeGenerationDiagnostics.UnknownOption, entry.KeyLocation, null, property.Name + "." + entry.Name);
                                    break;
                            }
                        }
                    }
                    else
                    {
                        Invalid(property, diagnostics, "expected an object with 'operations' and/or 'tags' arrays");
                    }

                    break;
                case "invalidations":
                    if (property.Value is ObjectNode invalidations)
                    {
                        options = options with
                        {
                            Invalidations = invalidations.Properties.ToDictionary(p => p.Name, p => List(p, diagnostics), StringComparer.Ordinal),
                        };
                    }
                    else
                    {
                        Invalid(property, diagnostics, "expected an object mapping mutation operationIds to arrays of query operationIds");
                    }

                    break;
                case "diagnostics":
                    if (property.Value is ObjectNode severities)
                    {
                        var map = new Dictionary<string, DiagnosticSeverity?>(StringComparer.Ordinal);
                        foreach (var entry in severities.Properties)
                        {
                            var text = Text(entry, diagnostics);
                            if (TryParseSeverity(text, out var severity))
                            {
                                map[entry.Name] = severity;
                            }
                            else
                            {
                                Invalid(entry, diagnostics, "expected none, info, warning or error");
                            }
                        }

                        options = options with { SeverityOverrides = map };
                    }
                    else
                    {
                        Invalid(property, diagnostics, "expected an object mapping diagnostic ids to none, info, warning or error");
                    }

                    break;
                default:
                    var value = property.Value is ScalarNode scalar ? (scalar.IsNull ? null : scalar.Text) : null;
                    if (value is null && property.Value is not ScalarNode)
                    {
                        Invalid(property, diagnostics, "expected a string, number or boolean");
                        break;
                    }

                    options = Apply(options, property.Name, value, location, diagnostics);
                    break;
            }
        }

        return configuration with { Options = options };
    }

    /// <summary>Applies <c>key=value</c> pairs separated by semicolons (MSBuild metadata syntax).</summary>
    public static GeneratorOptions ApplyPairs(GeneratorOptions options, string? pairs, DiagnosticBag diagnostics)
    {
        if (string.IsNullOrWhiteSpace(pairs))
        {
            return options;
        }

        foreach (var pair in pairs!.Split([';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                diagnostics.Report(CodeGenerationDiagnostics.InvalidOption, SourceLocation.None, null, pair.Trim(), string.Empty, "expected key=value");
                continue;
            }

            options = Apply(options, pair.Substring(0, equals).Trim(), pair.Substring(equals + 1).Trim(), SourceLocation.None, diagnostics);
        }

        return options;
    }

    /// <summary>Applies one option by name.</summary>
    public static GeneratorOptions Apply(GeneratorOptions options, string key, string? value, SourceLocation location, DiagnosticBag diagnostics)
    {
        bool Bool(bool fallback)
        {
            if (value is not null && bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            diagnostics.Report(CodeGenerationDiagnostics.InvalidOption, location, null, key, value, "expected true or false");
            return fallback;
        }

        T Enum<T>(T fallback)
            where T : struct
        {
            if (value is not null && System.Enum.TryParse<T>(value.Replace("-", string.Empty).Replace("_", string.Empty), ignoreCase: true, out var parsed) &&
                System.Enum.IsDefined(typeof(T), parsed))
            {
                return parsed;
            }

            diagnostics.Report(CodeGenerationDiagnostics.InvalidOption, location, null, key, value,
                "expected one of " + string.Join(", ", System.Enum.GetNames(typeof(T)).Select(n => n.ToLowerInvariant())));
            return fallback;
        }

        IReadOnlyList<string> Names() =>
            (value ?? string.Empty).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);

        switch (key.ToLowerInvariant())
        {
            case "namespace": return options with { Namespace = value ?? options.Namespace };
            case "modelsnamespace": return options with { ModelsNamespace = string.IsNullOrEmpty(value) ? null : value };
            case "clientname": return options with { ClientName = string.IsNullOrEmpty(value) ? null : value };
            case "grouping": return options with { Grouping = Enum(options.Grouping) };
            case "internaltypes":
            case "internal": return options with { InternalTypes = Bool(options.InternalTypes) };
            case "integerwithoutformat": return options with { IntegerWithoutFormat = Enum(options.IntegerWithoutFormat) };
            case "numberwithoutformat": return options with { NumberWithoutFormat = Enum(options.NumberWithoutFormat) };
            case "datetype": return options with { DateType = Enum(options.DateType) };
            case "datetimetype": return options with { DateTimeType = Enum(options.DateTimeType) };
            case "timetype": return options with { TimeType = Enum(options.TimeType) };
            case "collections": return options with { Collections = Enum(options.Collections) };
            case "optionalnullable": return options with { OptionalNullable = Enum(options.OptionalNullable) };
            case "userequiredmembers": return options with { UseRequiredMembers = Bool(options.UseRequiredMembers) };
            case "generateinterfaces": return options with { GenerateInterfaces = Bool(options.GenerateInterfaces) };
            case "generatedependencyinjection": return options with { GenerateDependencyInjection = Bool(options.GenerateDependencyInjection) };
            case "generatequeries": return options with { GenerateQueries = Bool(options.GenerateQueries) };
            case "generatemocks": return options with { GenerateMocks = Bool(options.GenerateMocks) };
            case "generatedataannotations": return options with { GenerateDataAnnotations = Bool(options.GenerateDataAnnotations) };
            case "generatedocumentation": return options with { GenerateDocumentation = Bool(options.GenerateDocumentation) };
            case "includeunusedmodels": return options with { IncludeUnusedModels = Bool(options.IncludeUnusedModels) };
            case "strict":
            case "treatwarningsaserrors": return options with { TreatWarningsAsErrors = Bool(options.TreatWarningsAsErrors) };
            case "includeoperations": return options with { IncludeOperations = Names() };
            case "excludeoperations": return options with { ExcludeOperations = Names() };
            case "includetags": return options with { IncludeTags = Names() };
            case "excludetags": return options with { ExcludeTags = Names() };
            default:
                diagnostics.Report(CodeGenerationDiagnostics.UnknownOption, location, null, key);
                return options;
        }
    }

    /// <summary>Parses a severity name (<c>none</c> suppresses).</summary>
    public static bool TryParseSeverity(string? text, out DiagnosticSeverity? severity)
    {
        severity = null;
        switch (text?.ToLowerInvariant())
        {
            case "none":
            case "hidden":
            case "suppress":
                return true;
            case "info":
            case "suggestion":
                severity = DiagnosticSeverity.Info;
                return true;
            case "warning":
                severity = DiagnosticSeverity.Warning;
                return true;
            case "error":
                severity = DiagnosticSeverity.Error;
                return true;
            default:
                return false;
        }
    }

    private static string? Text(PropertyNode property, DiagnosticBag diagnostics)
    {
        if (property.Value is ScalarNode scalar)
        {
            return scalar.IsNull ? null : scalar.Text;
        }

        Invalid(property, diagnostics, "expected a string");
        return null;
    }

    private static IReadOnlyList<string> List(PropertyNode property, DiagnosticBag diagnostics)
    {
        if (property.Value is ArrayNode array)
        {
            return array.Items.OfType<ScalarNode>().Select(s => s.Text).ToArray();
        }

        Invalid(property, diagnostics, "expected an array of strings");
        return [];
    }

    private static T ParseEnum<T>(PropertyNode property, T fallback, DiagnosticBag diagnostics)
        where T : struct
    {
        var text = Text(property, diagnostics);
        if (text is not null && Enum.TryParse<T>(text.Replace("-", string.Empty), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        Invalid(property, diagnostics, "expected one of " + string.Join(", ", Enum.GetNames(typeof(T)).Select(n => n.Substring(0, 1).ToLowerInvariant() + n.Substring(1))));
        return fallback;
    }

    private static void Invalid(PropertyNode property, DiagnosticBag diagnostics, string reason) =>
        diagnostics.Report(CodeGenerationDiagnostics.InvalidOption, property.KeyLocation, null, property.Name,
            property.Value is ScalarNode s ? s.Text : property.Value.ToJson(), reason);

    /// <summary>Formats a value for messages.</summary>
    internal static string Format(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
