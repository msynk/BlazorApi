using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Document;

/// <summary>The OpenAPI specification line a document follows.</summary>
public enum OpenApiSpecVersion
{
    /// <summary>OpenAPI 3.0.x (JSON Schema "extended subset", <c>nullable</c>).</summary>
    OpenApi3_0,

    /// <summary>OpenAPI 3.1.x (JSON Schema 2020-12). 3.2 documents are processed as 3.1.</summary>
    OpenApi3_1,
}

/// <summary>The root OpenAPI Object.</summary>
public sealed class OpenApiDocument : OpenApiElement
{
    /// <summary>The <c>openapi</c> version string as written.</summary>
    public string OpenApiVersion { get; internal set; } = string.Empty;

    /// <summary>The specification line.</summary>
    public OpenApiSpecVersion SpecVersion { get; internal set; }

    /// <summary>The <c>jsonSchemaDialect</c> (3.1).</summary>
    public string? JsonSchemaDialect { get; internal set; }

    /// <summary>API metadata.</summary>
    public OpenApiInfo Info { get; internal set; } = new();

    /// <summary>Servers.</summary>
    public IReadOnlyList<OpenApiServer> Servers { get; internal set; } = [];

    /// <summary>Path items keyed by path template, in document order.</summary>
    public OrderedMap<OpenApiPathItem> Paths { get; internal set; } = OrderedMap<OpenApiPathItem>.Empty;

    /// <summary>Webhooks (3.1), keyed by name.</summary>
    public OrderedMap<OpenApiPathItem> Webhooks { get; internal set; } = OrderedMap<OpenApiPathItem>.Empty;

    /// <summary>Reusable components.</summary>
    public OpenApiComponents Components { get; internal set; } = new();

    /// <summary>
    /// The default security requirements. An empty list means no security; each requirement is an alternative.
    /// </summary>
    public IReadOnlyList<OpenApiSecurityRequirement> Security { get; internal set; } = [];

    /// <summary>Tags with metadata.</summary>
    public IReadOnlyList<OpenApiTag> Tags { get; internal set; } = [];

    /// <summary>External documentation.</summary>
    public OpenApiExternalDocs? ExternalDocs { get; internal set; }

    /// <summary>Enumerates every operation in document order.</summary>
    public IEnumerable<OpenApiOperation> Operations =>
        Paths.Values.SelectMany(p => p.Resolved.Operations.Values);
}

/// <summary>The Info Object.</summary>
public sealed class OpenApiInfo : OpenApiElement
{
    /// <summary>The API title.</summary>
    public string Title { get; internal set; } = string.Empty;

    /// <summary>The API document version.</summary>
    public string Version { get; internal set; } = string.Empty;

    /// <summary>A short summary (3.1).</summary>
    public string? Summary { get; internal set; }

    /// <summary>A description (CommonMark).</summary>
    public string? Description { get; internal set; }

    /// <summary>The terms of service URL.</summary>
    public string? TermsOfService { get; internal set; }

    /// <summary>Contact information.</summary>
    public OpenApiContact? Contact { get; internal set; }

    /// <summary>License information.</summary>
    public OpenApiLicense? License { get; internal set; }
}

/// <summary>The Contact Object.</summary>
public sealed class OpenApiContact : OpenApiElement
{
    /// <summary>The contact name.</summary>
    public string? Name { get; internal set; }

    /// <summary>The contact URL.</summary>
    public string? Url { get; internal set; }

    /// <summary>The contact email.</summary>
    public string? Email { get; internal set; }
}

/// <summary>The License Object.</summary>
public sealed class OpenApiLicense : OpenApiElement
{
    /// <summary>The license name.</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>The SPDX identifier (3.1).</summary>
    public string? Identifier { get; internal set; }

    /// <summary>The license URL.</summary>
    public string? Url { get; internal set; }
}

/// <summary>The Server Object.</summary>
public sealed class OpenApiServer : OpenApiElement
{
    /// <summary>The URL template.</summary>
    public string Url { get; internal set; } = string.Empty;

    /// <summary>A description.</summary>
    public string? Description { get; internal set; }

    /// <summary>Template variables.</summary>
    public OrderedMap<OpenApiServerVariable> Variables { get; internal set; } = OrderedMap<OpenApiServerVariable>.Empty;
}

/// <summary>The Server Variable Object.</summary>
public sealed class OpenApiServerVariable : OpenApiElement
{
    /// <summary>The default value.</summary>
    public string Default { get; internal set; } = string.Empty;

    /// <summary>The allowed values.</summary>
    public IReadOnlyList<string> Enum { get; internal set; } = [];

    /// <summary>A description.</summary>
    public string? Description { get; internal set; }
}

/// <summary>The Tag Object.</summary>
public sealed class OpenApiTag : OpenApiElement
{
    /// <summary>The tag name.</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>A description.</summary>
    public string? Description { get; internal set; }

    /// <summary>External documentation.</summary>
    public OpenApiExternalDocs? ExternalDocs { get; internal set; }
}

/// <summary>The External Documentation Object.</summary>
public sealed class OpenApiExternalDocs : OpenApiElement
{
    /// <summary>The URL.</summary>
    public string Url { get; internal set; } = string.Empty;

    /// <summary>A description.</summary>
    public string? Description { get; internal set; }
}

/// <summary>The Components Object.</summary>
public sealed class OpenApiComponents : OpenApiElement
{
    /// <summary>Reusable schemas.</summary>
    public OrderedMap<OpenApiSchema> Schemas { get; internal set; } = OrderedMap<OpenApiSchema>.Empty;

    /// <summary>Reusable responses.</summary>
    public OrderedMap<OpenApiResponse> Responses { get; internal set; } = OrderedMap<OpenApiResponse>.Empty;

    /// <summary>Reusable parameters.</summary>
    public OrderedMap<OpenApiParameter> Parameters { get; internal set; } = OrderedMap<OpenApiParameter>.Empty;

    /// <summary>Reusable examples.</summary>
    public OrderedMap<OpenApiExample> Examples { get; internal set; } = OrderedMap<OpenApiExample>.Empty;

    /// <summary>Reusable request bodies.</summary>
    public OrderedMap<OpenApiRequestBody> RequestBodies { get; internal set; } = OrderedMap<OpenApiRequestBody>.Empty;

    /// <summary>Reusable headers.</summary>
    public OrderedMap<OpenApiHeader> Headers { get; internal set; } = OrderedMap<OpenApiHeader>.Empty;

    /// <summary>Security schemes.</summary>
    public OrderedMap<OpenApiSecurityScheme> SecuritySchemes { get; internal set; } = OrderedMap<OpenApiSecurityScheme>.Empty;

    /// <summary>Reusable path items (3.1).</summary>
    public OrderedMap<OpenApiPathItem> PathItems { get; internal set; } = OrderedMap<OpenApiPathItem>.Empty;

    /// <summary>The names of declared links (links are not used for generation).</summary>
    public IReadOnlyList<string> Links { get; internal set; } = [];

    /// <summary>The names of declared callbacks (callbacks are not used for generation).</summary>
    public IReadOnlyList<string> Callbacks { get; internal set; } = [];
}

/// <summary>The Example Object.</summary>
public sealed class OpenApiExample : OpenApiReferenceable<OpenApiExample>
{
    /// <summary>A short summary.</summary>
    public string? Summary { get; internal set; }

    /// <summary>A description.</summary>
    public string? Description { get; internal set; }

    /// <summary>The embedded example value.</summary>
    public DocNode? Value { get; internal set; }

    /// <summary>A URL pointing to the example value.</summary>
    public string? ExternalValue { get; internal set; }
}
