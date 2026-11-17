using BlazorApi.OpenApi.Diagnostics;

namespace BlazorApi.CodeGeneration;

/// <summary>How operations are grouped into client classes.</summary>
public enum ClientGrouping
{
    /// <summary>One client with every operation.</summary>
    Single,

    /// <summary>One client per (first) tag; untagged operations go to the default client.</summary>
    Tag,
}

/// <summary>The C# type used for <c>integer</c> schemas without a format.</summary>
public enum IntegerType
{
    /// <summary><see cref="int"/>.</summary>
    Int32,

    /// <summary><see cref="long"/> (the default: an unformatted integer is unbounded, so 64 bits avoids overflow).</summary>
    Int64,
}

/// <summary>The C# type used for <c>number</c> schemas without a format.</summary>
public enum NumberType
{
    /// <summary><see cref="double"/>.</summary>
    Double,

    /// <summary><see cref="decimal"/>.</summary>
    Decimal,
}

/// <summary>The C# type used for <c>format: date</c>.</summary>
public enum DateType
{
    /// <summary><c>System.DateOnly</c>.</summary>
    DateOnly,

    /// <summary><see cref="System.DateTimeOffset"/>.</summary>
    DateTimeOffset,

    /// <summary><see cref="string"/>.</summary>
    String,
}

/// <summary>The C# type used for <c>format: date-time</c>.</summary>
public enum DateTimeType
{
    /// <summary><see cref="System.DateTimeOffset"/> (keeps the offset).</summary>
    DateTimeOffset,

    /// <summary><see cref="System.DateTime"/>.</summary>
    DateTime,
}

/// <summary>The C# type used for <c>format: time</c> (RFC 3339 times may carry an offset).</summary>
public enum TimeType
{
    /// <summary><see cref="string"/> (the default, lossless).</summary>
    String,

    /// <summary><c>System.TimeOnly</c> (fails on values with an offset).</summary>
    TimeOnly,
}

/// <summary>The C# type used for <c>format: duration</c> (ISO 8601 durations such as <c>PT1H30M</c>).</summary>
public enum DurationType
{
    /// <summary><see cref="string"/> (the default, lossless: years and months are kept).</summary>
    String,

    /// <summary><see cref="System.TimeSpan"/>, read and written as ISO 8601 (years and months are rejected).</summary>
    TimeSpan,
}

/// <summary>The C# collection types used for arrays and maps.</summary>
public enum CollectionStyle
{
    /// <summary><c>IReadOnlyList&lt;T&gt;</c> and <c>IReadOnlyDictionary&lt;string, T&gt;</c>.</summary>
    ReadOnly,

    /// <summary><c>List&lt;T&gt;</c> and <c>Dictionary&lt;string, T&gt;</c>.</summary>
    Mutable,

    /// <summary><c>T[]</c> and <c>Dictionary&lt;string, T&gt;</c>.</summary>
    Array,
}

/// <summary>How optional properties whose value may be null are represented.</summary>
public enum OptionalNullableStyle
{
    /// <summary>
    /// <c>Optional&lt;T&gt;</c> (which tells "absent" from "null") for models used as JSON merge-patch bodies; a plain
    /// nullable property, omitted when null, elsewhere. The default.
    /// </summary>
    Auto,

    /// <summary>Always a plain nullable property, omitted from requests when null.</summary>
    Nullable,

    /// <summary>Always <c>Optional&lt;T&gt;</c>.</summary>
    Optional,
}

/// <summary>How generated enum converters read values the contract does not list.</summary>
public enum UnknownEnumValueHandling
{
    /// <summary>
    /// An unknown value fails with a <c>JsonException</c> naming the value (the default: the contract is the whole
    /// truth, and a new response value is reported by <c>api-gen diff</c> as potentially breaking).
    /// </summary>
    Fail,

    /// <summary>
    /// An unknown value reads as an extra <c>Unrecognized</c> member, so new values the server adds do not break
    /// deserialization (forward compatible). Sending <c>Unrecognized</c> fails, since it has no wire value.
    /// </summary>
    Unrecognized,
}

/// <summary>Code generation options. Every option has a default suited to most APIs.</summary>
public sealed record GeneratorOptions
{
    /// <summary>The namespace of generated code. Default: <c>ApiClient</c>.</summary>
    public string Namespace { get; init; } = "ApiClient";

    /// <summary>
    /// The namespace of generated models; <see langword="null"/> (the default) uses <see cref="Namespace"/>.
    /// </summary>
    public string? ModelsNamespace { get; init; }

    /// <summary>
    /// The client class name (with <see cref="ClientGrouping.Single"/>), or the default client's name for untagged
    /// operations. <see langword="null"/> derives it from the API title (<c>Petstore</c> becomes <c>PetstoreClient</c>).
    /// </summary>
    public string? ClientName { get; init; }

    /// <summary>How operations are grouped into clients. Default: one client.</summary>
    public ClientGrouping Grouping { get; init; } = ClientGrouping.Single;

    /// <summary>Whether generated types are <c>public</c> (default) or <c>internal</c>.</summary>
    public bool InternalTypes { get; init; }

    /// <summary>The type for unformatted integers. Default: <see cref="IntegerType.Int64"/>.</summary>
    public IntegerType IntegerWithoutFormat { get; init; } = IntegerType.Int64;

    /// <summary>The type for unformatted numbers. Default: <see cref="NumberType.Double"/>.</summary>
    public NumberType NumberWithoutFormat { get; init; } = NumberType.Double;

    /// <summary>The type for dates. Default: <see cref="DateType.DateOnly"/>.</summary>
    public DateType DateType { get; init; } = DateType.DateOnly;

    /// <summary>The type for date-times. Default: <see cref="DateTimeType.DateTimeOffset"/>.</summary>
    public DateTimeType DateTimeType { get; init; } = DateTimeType.DateTimeOffset;

    /// <summary>The type for times. Default: <see cref="TimeType.String"/>.</summary>
    public TimeType TimeType { get; init; } = TimeType.String;

    /// <summary>The type for durations. Default: <see cref="DurationType.String"/>.</summary>
    public DurationType DurationType { get; init; } = DurationType.String;

    /// <summary>The collection types. Default: read-only interfaces.</summary>
    public CollectionStyle Collections { get; init; } = CollectionStyle.ReadOnly;

    /// <summary>How optional nullable properties are represented. Default: <see cref="OptionalNullableStyle.Auto"/>.</summary>
    public OptionalNullableStyle OptionalNullable { get; init; } = OptionalNullableStyle.Auto;

    /// <summary>
    /// Whether required properties use the C# <c>required</c> modifier, which makes object initializers and the JSON
    /// serializer enforce them. Default: true.
    /// </summary>
    public bool UseRequiredMembers { get; init; } = true;

    /// <summary>How enums read values the contract does not list. Default: <see cref="UnknownEnumValueHandling.Fail"/>.</summary>
    public UnknownEnumValueHandling UnknownEnumValues { get; init; } = UnknownEnumValueHandling.Fail;

    /// <summary>
    /// Whether to generate a <c>System.Text.Json</c> source-generated <c>JsonSerializerContext</c> covering every type
    /// the clients serialize, for trimmed, native AOT and Blazor WebAssembly applications without reflection.
    /// Generated registration methods and default client options use it. Only for generated files compiled by the
    /// C# compiler (the CLI): the BlazorApi source generator cannot feed the System.Text.Json source generator, so
    /// it reports BAPI6005 and ignores the option. Default: false.
    /// </summary>
    public bool GenerateJsonContext { get; init; }

    /// <summary>
    /// Whether to generate <c>{Operation}ResponseHeaders</c> classes that read the response headers operations declare
    /// as typed values (default: true).
    /// </summary>
    public bool GenerateResponseHeaders { get; init; } = true;

    /// <summary>Whether to generate client interfaces (default: true).</summary>
    public bool GenerateInterfaces { get; init; } = true;

    /// <summary>Whether to generate <c>IServiceCollection</c> registration methods (default: true).</summary>
    public bool GenerateDependencyInjection { get; init; } = true;

    /// <summary>Whether to generate mock clients and test data builders (default: false).</summary>
    public bool GenerateMocks { get; init; }

    /// <summary>
    /// Whether to add <c>System.ComponentModel.DataAnnotations</c> attributes for schema constraints (required,
    /// lengths, ranges, patterns) so models validate in Blazor forms. Default: true.
    /// </summary>
    public bool GenerateDataAnnotations { get; init; } = true;

    /// <summary>Whether to generate XML documentation comments (default: true).</summary>
    public bool GenerateDocumentation { get; init; } = true;

    /// <summary>Operations to generate, by operationId; empty generates all (subject to the other filters).</summary>
    public IReadOnlyList<string> IncludeOperations { get; init; } = [];

    /// <summary>Operations to skip, by operationId.</summary>
    public IReadOnlyList<string> ExcludeOperations { get; init; } = [];

    /// <summary>Tags whose operations are generated; empty generates all.</summary>
    public IReadOnlyList<string> IncludeTags { get; init; } = [];

    /// <summary>Tags whose operations are skipped.</summary>
    public IReadOnlyList<string> ExcludeTags { get; init; } = [];

    /// <summary>
    /// Whether models not used by any generated operation are generated too. Default: true (every component
    /// schema); set false together with operation filters to generate a minimal set.
    /// </summary>
    public bool IncludeUnusedModels { get; init; } = true;

    /// <summary>Strict mode: warnings are reported as errors, so build-time validation fails on them. Default: false.</summary>
    public bool TreatWarningsAsErrors { get; init; }

    /// <summary>Per-diagnostic severity overrides; a <see langword="null"/> severity suppresses the diagnostic.</summary>
    public IReadOnlyDictionary<string, DiagnosticSeverity?> SeverityOverrides { get; init; } =
        new Dictionary<string, DiagnosticSeverity?>();

    /// <summary>The effective models namespace.</summary>
    public string EffectiveModelsNamespace => ModelsNamespace ?? Namespace;
}
