using BlazorApi.OpenApi.Diagnostics;

namespace BlazorApi.CodeGeneration;

/// <summary>Diagnostics produced by configuration (BAPI5xxx) and code generation (BAPI6xxx).</summary>
public static class CodeGenerationDiagnostics
{
    private const string Configuration = "Configuration";
    private const string Generation = "Generation";

    /// <summary>A configuration value is invalid.</summary>
    public static readonly DiagnosticDescriptor InvalidOption = new(
        "BAPI5001", "Invalid option", "Option '{0}' has an invalid value '{1}': {2}", DiagnosticSeverity.Error, Configuration);

    /// <summary>A configuration property is not recognized.</summary>
    public static readonly DiagnosticDescriptor UnknownOption = new(
        "BAPI5002", "Unknown option", "Option '{0}' is not recognized and is ignored", DiagnosticSeverity.Warning, Configuration);

    /// <summary>The configuration file could not be read.</summary>
    public static readonly DiagnosticDescriptor InvalidConfiguration = new(
        "BAPI5003", "Invalid configuration", "{0}", DiagnosticSeverity.Error, Configuration);

    /// <summary>Only one success response body type can be returned.</summary>
    public static readonly DiagnosticDescriptor MultipleSuccessTypes = new(
        "BAPI6001", "Multiple success body types", "{0} declares different success bodies; '{1}' ({2}) is returned and the others are not read", DiagnosticSeverity.Warning, Generation);

    /// <summary>Only one request body media type is used.</summary>
    public static readonly DiagnosticDescriptor RequestMediaTypesIgnored = new(
        "BAPI6002", "Request media types ignored", "{0} accepts several request media types; '{1}' is used", DiagnosticSeverity.Info, Generation);

    /// <summary>An operation filter matched nothing.</summary>
    public static readonly DiagnosticDescriptor FilterMatchedNothing = new(
        "BAPI6003", "Filter matched nothing", "{0} '{1}' does not match any operation", DiagnosticSeverity.Warning, Generation);

    /// <summary>The selected operations produce no code.</summary>
    public static readonly DiagnosticDescriptor NothingGenerated = new(
        "BAPI6004", "Nothing generated", "No operations were selected for generation", DiagnosticSeverity.Warning, Generation);
}
