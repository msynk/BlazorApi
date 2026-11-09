using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Model;

/// <summary>
/// The normalized, language-neutral description of an API: operations with merged and resolved parameters, typed
/// bodies and responses, and the named types they use. It is the single input of every code generator.
/// </summary>
public sealed class ApiModel
{
    /// <summary>The source document.</summary>
    public OpenApiDocument Document { get; internal set; } = null!;

    /// <summary>The API title.</summary>
    public string Title { get; internal set; } = string.Empty;

    /// <summary>The API document version.</summary>
    public string Version { get; internal set; } = string.Empty;

    /// <summary>The API description.</summary>
    public string? Description { get; internal set; }

    /// <summary>The servers.</summary>
    public IReadOnlyList<ApiServer> Servers { get; internal set; } = [];

    /// <summary>The operations in document order.</summary>
    public IReadOnlyList<ApiOperation> Operations { get; internal set; } = [];

    /// <summary>
    /// The named types: component schemas in document order, then inline types in the order operations and
    /// components first use them.
    /// </summary>
    public IReadOnlyList<ApiNamedType> Types { get; internal set; } = [];

    /// <summary>The security schemes in document order.</summary>
    public IReadOnlyList<ApiSecurityScheme> SecuritySchemes { get; internal set; } = [];

    /// <summary>The tags with metadata, in document order.</summary>
    public IReadOnlyList<OpenApiTag> Tags { get; internal set; } = [];
}

/// <summary>A server with its URL template.</summary>
public sealed class ApiServer
{
    internal ApiServer(string url, string? description, OrderedMap<OpenApiServerVariable> variables)
    {
        Url = url;
        Description = description;
        Variables = variables;
    }

    /// <summary>The URL template.</summary>
    public string Url { get; }

    /// <summary>A description.</summary>
    public string? Description { get; }

    /// <summary>Template variables.</summary>
    public OrderedMap<OpenApiServerVariable> Variables { get; }

    /// <summary>The URL with every variable replaced by its default value.</summary>
    public string DefaultUrl
    {
        get
        {
            var url = Url;
            foreach (var variable in Variables)
            {
                url = url.Replace("{" + variable.Key + "}", variable.Value.Default);
            }

            return url;
        }
    }
}

/// <summary>An API operation.</summary>
public sealed class ApiOperation
{
    internal ApiOperation(OpenApiOperation source) => Source = source;

    /// <summary>The source operation.</summary>
    public OpenApiOperation Source { get; }

    /// <summary>The operationId, when declared.</summary>
    public string? OperationId => Source.OperationId;

    /// <summary>
    /// The parts a method name is built from: the operationId, or the HTTP method followed by the literal and
    /// variable segments of the path.
    /// </summary>
    public IReadOnlyList<string> NameHint { get; internal set; } = [];

    /// <summary>The upper-case HTTP method.</summary>
    public string Method { get; internal set; } = string.Empty;

    /// <summary>The path template.</summary>
    public string Path { get; internal set; } = string.Empty;

    /// <summary>The summary.</summary>
    public string? Summary { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }

    /// <summary>Whether the operation is deprecated.</summary>
    public bool Deprecated { get; internal set; }

    /// <summary>The tags.</summary>
    public IReadOnlyList<string> Tags { get; internal set; } = [];

    /// <summary>
    /// The effective parameters (path-item parameters merged with operation parameters): path parameters in
    /// template order, then the others in declaration order.
    /// </summary>
    public IReadOnlyList<ApiParameter> Parameters { get; internal set; } = [];

    /// <summary>The request body.</summary>
    public ApiRequestBody? RequestBody { get; internal set; }

    /// <summary>The responses: specific status codes ascending, then ranges, then <c>default</c>.</summary>
    public IReadOnlyList<ApiResponse> Responses { get; internal set; } = [];

    /// <summary>
    /// The effective security requirements (alternatives). Empty means no authentication; a requirement with no
    /// schemes means anonymous access is allowed.
    /// </summary>
    public IReadOnlyList<ApiSecurityRequirement> Security { get; internal set; } = [];

    /// <summary>Servers overriding the document servers for this operation, if any.</summary>
    public IReadOnlyList<ApiServer> Servers { get; internal set; } = [];

    /// <summary>How the operation pages through results, when it does.</summary>
    public ApiPagination? Pagination { get; internal set; }

    /// <summary>The source location.</summary>
    public SourceLocation Location => Source.Location;

    /// <summary>The responses that represent success.</summary>
    public IEnumerable<ApiResponse> SuccessResponses => Responses.Where(r => r.IsSuccess);

    /// <inheritdoc />
    public override string ToString() => $"{Method} {Path}";
}

/// <summary>The serialization style of a parameter.</summary>
public enum ApiParameterStyle
{
    /// <summary>Comma-separated values (path, header).</summary>
    Simple,

    /// <summary><c>name=value</c> pairs (query, cookie).</summary>
    Form,

    /// <summary><c>;name=value</c> (path).</summary>
    Matrix,

    /// <summary><c>.value</c> (path).</summary>
    Label,

    /// <summary>Space-separated array values (query).</summary>
    SpaceDelimited,

    /// <summary>Pipe-separated array values (query).</summary>
    PipeDelimited,

    /// <summary><c>name[key]=value</c> for objects (query).</summary>
    DeepObject,
}

/// <summary>An operation parameter.</summary>
public sealed class ApiParameter
{
    internal ApiParameter(OpenApiParameter source, string name, ParameterLocation location, ApiTypeRef type)
    {
        Source = source;
        Name = name;
        Location = location;
        Type = type;
    }

    /// <summary>The source parameter (resolved).</summary>
    public OpenApiParameter Source { get; }

    /// <summary>The wire name.</summary>
    public string Name { get; }

    /// <summary>Where the parameter goes.</summary>
    public ParameterLocation Location { get; }

    /// <summary>The value type.</summary>
    public ApiTypeRef Type { get; }

    /// <summary>Whether the parameter is required.</summary>
    public bool Required { get; internal set; }

    /// <summary>Whether the parameter is deprecated.</summary>
    public bool Deprecated { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }

    /// <summary>The serialization style (with the location default applied).</summary>
    public ApiParameterStyle Style { get; internal set; }

    /// <summary>The explode flag (with the style default applied).</summary>
    public bool Explode { get; internal set; }

    /// <summary>Whether reserved characters are sent without percent-encoding.</summary>
    public bool AllowReserved { get; internal set; }

    /// <summary>
    /// The media type when the parameter is described by <c>content</c> (its value is serialized with that media
    /// type, typically JSON) instead of a style.
    /// </summary>
    public string? ContentMediaType { get; internal set; }

    /// <summary>The default value.</summary>
    public DocNode? Default { get; internal set; }

    /// <summary>Validation constraints.</summary>
    public ApiConstraints Constraints { get; internal set; } = ApiConstraints.None;

    /// <inheritdoc />
    public override string ToString() => $"{Location.ToString().ToLowerInvariant()} {Name}: {Type}";
}

/// <summary>How a body is encoded.</summary>
public enum ApiBodyKind
{
    /// <summary>JSON (<c>application/json</c> and <c>+json</c> types).</summary>
    Json,

    /// <summary>Plain text (<c>text/*</c>).</summary>
    Text,

    /// <summary>URL-encoded form fields.</summary>
    FormUrlEncoded,

    /// <summary>Multipart form data.</summary>
    Multipart,

    /// <summary>Raw bytes (files, images, octet streams).</summary>
    Binary,

    /// <summary>A media type without typed support (for example XML); handled as text.</summary>
    Other,
}

/// <summary>A body representation for one media type.</summary>
public sealed class ApiContent
{
    internal ApiContent(string mediaType, ApiBodyKind kind, ApiTypeRef? type, OpenApiMediaType source)
    {
        MediaType = mediaType;
        Kind = kind;
        Type = type;
        Source = source;
    }

    /// <summary>The media type (range).</summary>
    public string MediaType { get; }

    /// <summary>The body kind.</summary>
    public ApiBodyKind Kind { get; }

    /// <summary>The body type, or <see langword="null"/> when no schema is declared.</summary>
    public ApiTypeRef? Type { get; }

    /// <summary>The source media type object.</summary>
    public OpenApiMediaType Source { get; }

    /// <summary>The parts of a multipart or form body, in property order.</summary>
    public IReadOnlyList<ApiBodyPart> Parts { get; internal set; } = [];

    /// <inheritdoc />
    public override string ToString() => $"{MediaType} ({Kind}): {Type?.ToString() ?? "-"}";
}

/// <summary>A field of a multipart or URL-encoded form body.</summary>
public sealed class ApiBodyPart
{
    internal ApiBodyPart(string name, ApiTypeRef type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>The field name.</summary>
    public string Name { get; }

    /// <summary>The field type (<see cref="ApiPrimitiveKind.Binary"/> for files).</summary>
    public ApiTypeRef Type { get; }

    /// <summary>Whether the field is required.</summary>
    public bool Required { get; internal set; }

    /// <summary>The part content type from the encoding object, when declared.</summary>
    public string? ContentType { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }

    /// <summary>Whether the part carries a file (binary content).</summary>
    public bool IsFile => Type is ApiPrimitiveType { Kind: ApiPrimitiveKind.Binary } ||
        Type is ApiArrayType { Items: ApiPrimitiveType { Kind: ApiPrimitiveKind.Binary } };
}

/// <summary>A request body.</summary>
public sealed class ApiRequestBody
{
    /// <summary>Whether the body is required.</summary>
    public bool Required { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }

    /// <summary>The representations in document order.</summary>
    public IReadOnlyList<ApiContent> Contents { get; internal set; } = [];
}

/// <summary>A response.</summary>
public sealed class ApiResponse
{
    internal ApiResponse(string statusCode) => StatusCode = statusCode;

    /// <summary>The status key: <c>200</c>, <c>4XX</c> or <c>default</c>.</summary>
    public string StatusCode { get; }

    /// <summary>The numeric status for specific codes; <see langword="null"/> for ranges and <c>default</c>.</summary>
    public int? Status { get; internal set; }

    /// <summary>The status class (1-5) for codes and ranges; <see langword="null"/> for <c>default</c>.</summary>
    public int? StatusClass { get; internal set; }

    /// <summary>Whether this is the <c>default</c> response.</summary>
    public bool IsDefault => StatusCode == "default";

    /// <summary>
    /// Whether the response represents success: 2xx codes and ranges, or <c>default</c> when the operation declares
    /// no 2xx response.
    /// </summary>
    public bool IsSuccess { get; internal set; }

    /// <summary>The description.</summary>
    public string Description { get; internal set; } = string.Empty;

    /// <summary>The representations in document order.</summary>
    public IReadOnlyList<ApiContent> Contents { get; internal set; } = [];

    /// <summary>The response headers.</summary>
    public IReadOnlyList<ApiHeader> Headers { get; internal set; } = [];

    /// <inheritdoc />
    public override string ToString() => StatusCode + (Contents.Count > 0 ? ": " + Contents[0] : string.Empty);
}

/// <summary>A response header.</summary>
public sealed class ApiHeader
{
    internal ApiHeader(string name, ApiTypeRef type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>The header name.</summary>
    public string Name { get; }

    /// <summary>The value type.</summary>
    public ApiTypeRef Type { get; }

    /// <summary>Whether the header is always present.</summary>
    public bool Required { get; internal set; }

    /// <summary>The description.</summary>
    public string? Description { get; internal set; }
}

/// <summary>A security scheme.</summary>
public sealed class ApiSecurityScheme
{
    internal ApiSecurityScheme(string name, OpenApiSecurityScheme source)
    {
        Name = name;
        Source = source;
    }

    /// <summary>The scheme name (its key in <c>components/securitySchemes</c>).</summary>
    public string Name { get; }

    /// <summary>The source scheme (resolved).</summary>
    public OpenApiSecurityScheme Source { get; }

    /// <summary>The scheme type.</summary>
    public SecuritySchemeType? Type => Source.Type;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Type})";
}

/// <summary>A security requirement: every listed scheme must be satisfied.</summary>
public sealed class ApiSecurityRequirement
{
    internal ApiSecurityRequirement(IReadOnlyList<ApiSecurityRequirementItem> items) => Items = items;

    /// <summary>The schemes and scopes; empty for anonymous access.</summary>
    public IReadOnlyList<ApiSecurityRequirementItem> Items { get; }

    /// <summary>Whether this requirement allows anonymous access.</summary>
    public bool IsAnonymous => Items.Count == 0;
}

/// <summary>A scheme within a security requirement.</summary>
public sealed class ApiSecurityRequirementItem
{
    internal ApiSecurityRequirementItem(ApiSecurityScheme scheme, IReadOnlyList<string> scopes)
    {
        Scheme = scheme;
        Scopes = scopes;
    }

    /// <summary>The scheme.</summary>
    public ApiSecurityScheme Scheme { get; }

    /// <summary>The required scopes or roles.</summary>
    public IReadOnlyList<string> Scopes { get; }
}
