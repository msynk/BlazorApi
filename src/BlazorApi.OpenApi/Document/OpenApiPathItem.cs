using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Document;

/// <summary>The Path Item Object.</summary>
public sealed class OpenApiPathItem : OpenApiReferenceable<OpenApiPathItem>
{
    /// <summary>The path template (<c>/pets/{petId}</c>), or the webhook name.</summary>
    public string Path { get; internal set; } = string.Empty;

    /// <summary>A summary for all operations.</summary>
    public string? Summary { get; internal set; }

    /// <summary>A description for all operations.</summary>
    public string? Description { get; internal set; }

    /// <summary>Operations keyed by lower-case HTTP method, in document order.</summary>
    public OrderedMap<OpenApiOperation> Operations { get; internal set; } = OrderedMap<OpenApiOperation>.Empty;

    /// <summary>Servers overriding the document servers.</summary>
    public IReadOnlyList<OpenApiServer> Servers { get; internal set; } = [];

    /// <summary>Parameters shared by all operations.</summary>
    public IReadOnlyList<OpenApiParameter> Parameters { get; internal set; } = [];
}

/// <summary>The Operation Object.</summary>
public sealed class OpenApiOperation : OpenApiElement
{
    /// <summary>The lower-case HTTP method (<c>get</c>, <c>post</c>, ...).</summary>
    public string Method { get; internal set; } = string.Empty;

    /// <summary>The path template of the owning path item.</summary>
    public string Path { get; internal set; } = string.Empty;

    /// <summary>The owning path item.</summary>
    public OpenApiPathItem PathItem { get; internal set; } = null!;

    /// <summary>The operation identifier.</summary>
    public string? OperationId { get; internal set; }

    /// <summary>A short summary.</summary>
    public string? Summary { get; internal set; }

    /// <summary>A description.</summary>
    public string? Description { get; internal set; }

    /// <summary>Tags.</summary>
    public IReadOnlyList<string> Tags { get; internal set; } = [];

    /// <summary>Operation-level parameters (path-item parameters are merged by the API model).</summary>
    public IReadOnlyList<OpenApiParameter> Parameters { get; internal set; } = [];

    /// <summary>The request body.</summary>
    public OpenApiRequestBody? RequestBody { get; internal set; }

    /// <summary>Responses keyed by status code (<c>200</c>, <c>4XX</c>, <c>default</c>), in document order.</summary>
    public OrderedMap<OpenApiResponse> Responses { get; internal set; } = OrderedMap<OpenApiResponse>.Empty;

    /// <summary>Whether the operation is deprecated.</summary>
    public bool Deprecated { get; internal set; }

    /// <summary>
    /// The security requirements, or <see langword="null"/> to inherit the document's. An empty list removes
    /// security for this operation.
    /// </summary>
    public IReadOnlyList<OpenApiSecurityRequirement>? Security { get; internal set; }

    /// <summary>Servers overriding the path item and document servers.</summary>
    public IReadOnlyList<OpenApiServer> Servers { get; internal set; } = [];

    /// <summary>External documentation.</summary>
    public OpenApiExternalDocs? ExternalDocs { get; internal set; }

    /// <summary>The names of declared callbacks (callbacks are not used for generation).</summary>
    public IReadOnlyList<string> Callbacks { get; internal set; } = [];

    /// <summary>A display name for messages: the operationId, or <c>GET /path</c>.</summary>
    public string DisplayName => OperationId is null
        ? $"{Method.ToUpperInvariant()} {Path}"
        : $"operation '{OperationId}' ({Method.ToUpperInvariant()} {Path})";
}

/// <summary>The location of a parameter.</summary>
public enum ParameterLocation
{
    /// <summary>The query string.</summary>
    Query,

    /// <summary>A request header.</summary>
    Header,

    /// <summary>A path template segment.</summary>
    Path,

    /// <summary>A cookie.</summary>
    Cookie,
}

/// <summary>The properties shared by the Parameter and Header objects.</summary>
public abstract class OpenApiParameterBase<T> : OpenApiReferenceable<T>
    where T : OpenApiParameterBase<T>
{
    /// <summary>A description.</summary>
    public string? Description { get; internal set; }

    /// <summary>Whether the parameter is required.</summary>
    public bool Required { get; internal set; }

    /// <summary>Whether the parameter is deprecated.</summary>
    public bool Deprecated { get; internal set; }

    /// <summary>The serialization style as written, or <see langword="null"/> for the location default.</summary>
    public string? Style { get; internal set; }

    /// <summary>The <c>explode</c> flag as written, or <see langword="null"/> for the style default.</summary>
    public bool? Explode { get; internal set; }

    /// <summary>The schema, when the parameter is described by a schema.</summary>
    public OpenApiSchema? Schema { get; internal set; }

    /// <summary>The content map (exactly one entry), when the parameter is described by a media type.</summary>
    public OrderedMap<OpenApiMediaType> Content { get; internal set; } = OrderedMap<OpenApiMediaType>.Empty;

    /// <summary>An example value.</summary>
    public DocNode? Example { get; internal set; }

    /// <summary>Named examples.</summary>
    public OrderedMap<OpenApiExample> Examples { get; internal set; } = OrderedMap<OpenApiExample>.Empty;
}

/// <summary>The Parameter Object.</summary>
public sealed class OpenApiParameter : OpenApiParameterBase<OpenApiParameter>
{
    /// <summary>The parameter name (case-sensitive except for headers).</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>The location, or <see langword="null"/> when missing or invalid.</summary>
    public ParameterLocation? In { get; internal set; }

    /// <summary>Whether an empty value is allowed (query only, deprecated by the specification).</summary>
    public bool AllowEmptyValue { get; internal set; }

    /// <summary>Whether reserved characters are sent unencoded (query only).</summary>
    public bool AllowReserved { get; internal set; }
}

/// <summary>The Header Object.</summary>
public sealed class OpenApiHeader : OpenApiParameterBase<OpenApiHeader>
{
}

/// <summary>The Request Body Object.</summary>
public sealed class OpenApiRequestBody : OpenApiReferenceable<OpenApiRequestBody>
{
    /// <summary>A description.</summary>
    public string? Description { get; internal set; }

    /// <summary>The content keyed by media type (range), in document order.</summary>
    public OrderedMap<OpenApiMediaType> Content { get; internal set; } = OrderedMap<OpenApiMediaType>.Empty;

    /// <summary>Whether the body is required.</summary>
    public bool Required { get; internal set; }
}

/// <summary>The Media Type Object.</summary>
public sealed class OpenApiMediaType : OpenApiElement
{
    /// <summary>The media type (range) key, for example <c>application/json</c>.</summary>
    public string MediaType { get; internal set; } = string.Empty;

    /// <summary>The schema.</summary>
    public OpenApiSchema? Schema { get; internal set; }

    /// <summary>An example value.</summary>
    public DocNode? Example { get; internal set; }

    /// <summary>Named examples.</summary>
    public OrderedMap<OpenApiExample> Examples { get; internal set; } = OrderedMap<OpenApiExample>.Empty;

    /// <summary>Per-property encodings (multipart and form bodies).</summary>
    public OrderedMap<OpenApiEncoding> Encoding { get; internal set; } = OrderedMap<OpenApiEncoding>.Empty;
}

/// <summary>The Encoding Object.</summary>
public sealed class OpenApiEncoding : OpenApiElement
{
    /// <summary>The content type of the part.</summary>
    public string? ContentType { get; internal set; }

    /// <summary>Part headers.</summary>
    public OrderedMap<OpenApiHeader> Headers { get; internal set; } = OrderedMap<OpenApiHeader>.Empty;

    /// <summary>The serialization style (form bodies).</summary>
    public string? Style { get; internal set; }

    /// <summary>The explode flag (form bodies).</summary>
    public bool? Explode { get; internal set; }

    /// <summary>Whether reserved characters are sent unencoded (form bodies).</summary>
    public bool AllowReserved { get; internal set; }
}

/// <summary>The Response Object.</summary>
public sealed class OpenApiResponse : OpenApiReferenceable<OpenApiResponse>
{
    /// <summary>The description (required by the specification).</summary>
    public string Description { get; internal set; } = string.Empty;

    /// <summary>Response headers.</summary>
    public OrderedMap<OpenApiHeader> Headers { get; internal set; } = OrderedMap<OpenApiHeader>.Empty;

    /// <summary>The content keyed by media type (range), in document order.</summary>
    public OrderedMap<OpenApiMediaType> Content { get; internal set; } = OrderedMap<OpenApiMediaType>.Empty;

    /// <summary>The names of declared links (links are not used for generation).</summary>
    public IReadOnlyList<string> Links { get; internal set; } = [];
}
