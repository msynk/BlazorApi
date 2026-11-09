namespace BlazorApi.OpenApi.Diagnostics;

/// <summary>
/// The catalog of diagnostics produced by the OpenAPI processing engine. Identifier ranges:
/// BAPI1xxx syntax and document structure, BAPI2xxx references, BAPI3xxx semantic validation, BAPI4xxx API model
/// construction. Configuration (BAPI5xxx) and code generation (BAPI6xxx) diagnostics are defined by the code generator.
/// </summary>
public static class DiagnosticDescriptors
{
    private const string Syntax = "Syntax";
    private const string Reference = "Reference";
    private const string Validation = "Validation";
    private const string Model = "Model";

    // ---- BAPI1xxx: syntax and document structure ----

    /// <summary>The document is not well-formed JSON or YAML.</summary>
    public static readonly DiagnosticDescriptor SyntaxError = new(
        "BAPI1001", "Syntax error", "{0}", DiagnosticSeverity.Error, Syntax);

    /// <summary>A configured safety limit (size, depth, alias expansion) was exceeded.</summary>
    public static readonly DiagnosticDescriptor LimitExceeded = new(
        "BAPI1002", "Document limit exceeded", "{0}", DiagnosticSeverity.Error, Syntax);

    /// <summary>The OpenAPI version is not supported.</summary>
    public static readonly DiagnosticDescriptor UnsupportedVersion = new(
        "BAPI1003", "Unsupported OpenAPI version", "OpenAPI version '{0}' is not supported; expected 3.0.x or 3.1.x{1}", DiagnosticSeverity.Error, Syntax);

    /// <summary>The document has no <c>openapi</c> field.</summary>
    public static readonly DiagnosticDescriptor MissingVersion = new(
        "BAPI1004", "Missing OpenAPI version", "The document has no 'openapi' version field{0}", DiagnosticSeverity.Error, Syntax);

    /// <summary>A mapping contains the same key twice.</summary>
    public static readonly DiagnosticDescriptor DuplicateKey = new(
        "BAPI1005", "Duplicate key", "Duplicate key '{0}'; the last value is used", DiagnosticSeverity.Warning, Syntax);

    /// <summary>A value has the wrong JSON type for its field.</summary>
    public static readonly DiagnosticDescriptor UnexpectedNodeType = new(
        "BAPI1006", "Unexpected value type", "'{0}' must be {1} but is {2}", DiagnosticSeverity.Error, Syntax);

    /// <summary>A field the specification requires is missing.</summary>
    public static readonly DiagnosticDescriptor RequiredFieldMissing = new(
        "BAPI1007", "Required field missing", "{0} requires the field '{1}'", DiagnosticSeverity.Error, Syntax);

    /// <summary>A field that is neither defined by the specification nor an <c>x-</c> extension.</summary>
    public static readonly DiagnosticDescriptor UnknownField = new(
        "BAPI1008", "Unknown field", "'{0}' is not a field of {1} and is ignored (extensions must start with 'x-')", DiagnosticSeverity.Warning, Syntax);

    /// <summary>A field value is invalid.</summary>
    public static readonly DiagnosticDescriptor InvalidValue = new(
        "BAPI1009", "Invalid value", "Invalid value '{0}' for '{1}': {2}", DiagnosticSeverity.Error, Syntax);

    /// <summary>A source document could not be read.</summary>
    public static readonly DiagnosticDescriptor SourceUnavailable = new(
        "BAPI1010", "Source unavailable", "Cannot read '{0}': {1}", DiagnosticSeverity.Error, Syntax);

    /// <summary>A YAML construct is not supported.</summary>
    public static readonly DiagnosticDescriptor UnsupportedYamlFeature = new(
        "BAPI1011", "Unsupported YAML feature", "{0}", DiagnosticSeverity.Error, Syntax);

    // ---- BAPI2xxx: references ----

    /// <summary>A <c>$ref</c> target does not exist.</summary>
    public static readonly DiagnosticDescriptor UnresolvedReference = new(
        "BAPI2001", "Unresolved reference", "Reference '{0}' cannot be resolved: {1}", DiagnosticSeverity.Error, Reference);

    /// <summary>A <c>$ref</c> points to a location the reference policy does not allow.</summary>
    public static readonly DiagnosticDescriptor ReferenceNotAllowed = new(
        "BAPI2002", "Reference not allowed", "Reference '{0}' is not allowed: {1}", DiagnosticSeverity.Error, Reference);

    /// <summary>References that only point at each other and never reach a definition.</summary>
    public static readonly DiagnosticDescriptor ReferenceCycle = new(
        "BAPI2003", "Reference cycle", "Reference '{0}' is part of a cycle that never reaches a definition", DiagnosticSeverity.Error, Reference);

    /// <summary>A <c>$ref</c> value is malformed.</summary>
    public static readonly DiagnosticDescriptor InvalidReference = new(
        "BAPI2004", "Invalid reference", "Reference '{0}' is malformed: {1}", DiagnosticSeverity.Error, Reference);

    /// <summary>A reference form that is recognized but not supported.</summary>
    public static readonly DiagnosticDescriptor UnsupportedReference = new(
        "BAPI2005", "Unsupported reference", "Reference '{0}' is not supported: {1}", DiagnosticSeverity.Error, Reference);

    // ---- BAPI3xxx: semantic validation ----

    /// <summary>Two operations share an <c>operationId</c>.</summary>
    public static readonly DiagnosticDescriptor DuplicateOperationId = new(
        "BAPI3001", "Duplicate operationId", "operationId '{0}' is also used by {1}", DiagnosticSeverity.Error, Validation);

    /// <summary>A path template variable has no matching path parameter.</summary>
    public static readonly DiagnosticDescriptor PathParameterNotDeclared = new(
        "BAPI3002", "Path parameter not declared", "Path template variable '{{{0}}}' of {1} has no 'in: path' parameter", DiagnosticSeverity.Error, Validation);

    /// <summary>A path parameter does not appear in the path template.</summary>
    public static readonly DiagnosticDescriptor PathParameterNotInTemplate = new(
        "BAPI3003", "Path parameter not in template", "Path parameter '{0}' of {1} does not appear in the path template", DiagnosticSeverity.Error, Validation);

    /// <summary>A path parameter is not marked required.</summary>
    public static readonly DiagnosticDescriptor PathParameterNotRequired = new(
        "BAPI3004", "Path parameter not required", "Path parameter '{0}' of {1} must have 'required: true'; it is treated as required", DiagnosticSeverity.Warning, Validation);

    /// <summary>An operation declares the same parameter (name and location) twice.</summary>
    public static readonly DiagnosticDescriptor DuplicateParameter = new(
        "BAPI3005", "Duplicate parameter", "Parameter '{0}' in {1} is declared more than once in {2}", DiagnosticSeverity.Error, Validation);

    /// <summary>An operation declares no responses.</summary>
    public static readonly DiagnosticDescriptor MissingResponses = new(
        "BAPI3006", "No responses", "{0} declares no responses", DiagnosticSeverity.Warning, Validation);

    /// <summary>A security requirement names an undefined scheme.</summary>
    public static readonly DiagnosticDescriptor UnknownSecurityScheme = new(
        "BAPI3007", "Unknown security scheme", "Security requirement references undefined scheme '{0}'", DiagnosticSeverity.Error, Validation);

    /// <summary>A path template is malformed.</summary>
    public static readonly DiagnosticDescriptor InvalidPathTemplate = new(
        "BAPI3008", "Invalid path template", "Path '{0}' is invalid: {1}", DiagnosticSeverity.Error, Validation);

    /// <summary>A <c>required</c> entry names a property the schema does not define.</summary>
    public static readonly DiagnosticDescriptor RequiredPropertyNotDefined = new(
        "BAPI3009", "Required property not defined", "Required property '{0}' is not defined in 'properties'", DiagnosticSeverity.Warning, Validation);

    /// <summary>A discriminator names a property that the schema or its variants do not define.</summary>
    public static readonly DiagnosticDescriptor DiscriminatorPropertyMissing = new(
        "BAPI3010", "Discriminator property missing", "Discriminator property '{0}' is not defined by {1}", DiagnosticSeverity.Warning, Validation);

    /// <summary>Two paths differ only in template variable names.</summary>
    public static readonly DiagnosticDescriptor EquivalentPaths = new(
        "BAPI3011", "Equivalent paths", "Path '{0}' is equivalent to '{1}' (only template variable names differ)", DiagnosticSeverity.Error, Validation);

    /// <summary>A parameter style is not allowed for its location or type.</summary>
    public static readonly DiagnosticDescriptor InvalidParameterStyle = new(
        "BAPI3012", "Invalid parameter style", "Style '{0}' is not valid for {1} parameter '{2}'", DiagnosticSeverity.Error, Validation);

    /// <summary>A parameter has both or neither of <c>schema</c> and <c>content</c>.</summary>
    public static readonly DiagnosticDescriptor ParameterSchemaOrContent = new(
        "BAPI3013", "Parameter schema or content", "Parameter '{0}' must have exactly one of 'schema' or 'content'{1}", DiagnosticSeverity.Error, Validation);

    /// <summary>A default, enum or example value does not match the schema type.</summary>
    public static readonly DiagnosticDescriptor ValueTypeMismatch = new(
        "BAPI3014", "Value does not match schema", "{0} value '{1}' does not match schema type '{2}'", DiagnosticSeverity.Warning, Validation);

    /// <summary>A <c>pattern</c> is not a valid regular expression.</summary>
    public static readonly DiagnosticDescriptor InvalidPattern = new(
        "BAPI3015", "Invalid pattern", "Pattern '{0}' is not a valid regular expression: {1}", DiagnosticSeverity.Warning, Validation);

    /// <summary>A server URL template variable is not defined.</summary>
    public static readonly DiagnosticDescriptor ServerVariableNotDefined = new(
        "BAPI3016", "Server variable not defined", "Server URL '{0}' uses variable '{1}' that is not defined in 'variables'", DiagnosticSeverity.Error, Validation);

    /// <summary>A link, callback or other construct that the generator ignores.</summary>
    public static readonly DiagnosticDescriptor FeatureIgnored = new(
        "BAPI3017", "Feature ignored", "{0} is not used for code generation", DiagnosticSeverity.Info, Validation);

    /// <summary>Invalid constraint combination (for example minLength greater than maxLength).</summary>
    public static readonly DiagnosticDescriptor InvalidConstraint = new(
        "BAPI3018", "Invalid constraint", "{0}", DiagnosticSeverity.Warning, Validation);

    // ---- BAPI4xxx: API model construction ----

    /// <summary>A schema keyword that cannot be represented; the value is typed as untyped JSON instead.</summary>
    public static readonly DiagnosticDescriptor UnsupportedSchemaKeyword = new(
        "BAPI4001", "Unsupported schema keyword", "Schema keyword '{0}' is not supported; {1}", DiagnosticSeverity.Warning, Model);

    /// <summary>A union that cannot be told apart at runtime.</summary>
    public static readonly DiagnosticDescriptor AmbiguousUnion = new(
        "BAPI4002", "Ambiguous union", "{0}", DiagnosticSeverity.Warning, Model);

    /// <summary>allOf members define the same property with different types.</summary>
    public static readonly DiagnosticDescriptor ConflictingAllOfProperty = new(
        "BAPI4003", "Conflicting allOf property", "Property '{0}' is defined with different schemas by allOf members; the first definition is used", DiagnosticSeverity.Warning, Model);

    /// <summary>A media type that has no typed representation.</summary>
    public static readonly DiagnosticDescriptor UnsupportedMediaType = new(
        "BAPI4004", "Unsupported media type", "Media type '{0}' {1}", DiagnosticSeverity.Info, Model);

    /// <summary>A schema allows several unrelated JSON types.</summary>
    public static readonly DiagnosticDescriptor MultipleTypes = new(
        "BAPI4005", "Multiple types", "Schema allows types [{0}]; it is typed as untyped JSON", DiagnosticSeverity.Warning, Model);

    /// <summary>An enum value that cannot be represented.</summary>
    public static readonly DiagnosticDescriptor InvalidEnumValue = new(
        "BAPI4006", "Invalid enum value", "Enum value '{0}' {1}", DiagnosticSeverity.Warning, Model);

    /// <summary>A format that is not recognized; the base type is used.</summary>
    public static readonly DiagnosticDescriptor UnknownFormat = new(
        "BAPI4007", "Unknown format", "Format '{0}' of type '{1}' is not recognized; the plain '{1}' mapping is used", DiagnosticSeverity.Info, Model);

    /// <summary>A parameter serialization that the runtime cannot produce.</summary>
    public static readonly DiagnosticDescriptor UnsupportedParameterSerialization = new(
        "BAPI4008", "Unsupported parameter serialization", "Parameter '{0}': {1}", DiagnosticSeverity.Warning, Model);

    /// <summary>A discriminator mapping value that cannot be resolved to a variant.</summary>
    public static readonly DiagnosticDescriptor InvalidDiscriminatorMapping = new(
        "BAPI4009", "Invalid discriminator mapping", "Discriminator mapping '{0}' -> '{1}' {2}", DiagnosticSeverity.Warning, Model);
}
