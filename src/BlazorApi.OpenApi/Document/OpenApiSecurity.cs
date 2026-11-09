namespace BlazorApi.OpenApi.Document;

/// <summary>The type of a security scheme.</summary>
public enum SecuritySchemeType
{
    /// <summary>An API key in a header, query parameter or cookie.</summary>
    ApiKey,

    /// <summary>HTTP authentication (<c>bearer</c>, <c>basic</c>, ...).</summary>
    Http,

    /// <summary>OAuth 2.0.</summary>
    OAuth2,

    /// <summary>OpenID Connect discovery.</summary>
    OpenIdConnect,

    /// <summary>Mutual TLS (3.1).</summary>
    MutualTls,
}

/// <summary>The Security Scheme Object.</summary>
public sealed class OpenApiSecurityScheme : OpenApiReferenceable<OpenApiSecurityScheme>
{
    /// <summary>The scheme type, or <see langword="null"/> when missing or invalid.</summary>
    public SecuritySchemeType? Type { get; internal set; }

    /// <summary>A description.</summary>
    public string? Description { get; internal set; }

    /// <summary>The API key name (apiKey).</summary>
    public string? Name { get; internal set; }

    /// <summary>The API key location (apiKey): header, query or cookie.</summary>
    public ParameterLocation? In { get; internal set; }

    /// <summary>The HTTP authorization scheme name (http), for example <c>bearer</c>.</summary>
    public string? Scheme { get; internal set; }

    /// <summary>A hint about the bearer token format (http bearer).</summary>
    public string? BearerFormat { get; internal set; }

    /// <summary>The OAuth flows (oauth2).</summary>
    public OpenApiOAuthFlows? Flows { get; internal set; }

    /// <summary>The OpenID Connect discovery URL (openIdConnect).</summary>
    public string? OpenIdConnectUrl { get; internal set; }
}

/// <summary>The OAuth Flows Object.</summary>
public sealed class OpenApiOAuthFlows : OpenApiElement
{
    /// <summary>The implicit flow.</summary>
    public OpenApiOAuthFlow? Implicit { get; internal set; }

    /// <summary>The resource owner password flow.</summary>
    public OpenApiOAuthFlow? Password { get; internal set; }

    /// <summary>The client credentials flow.</summary>
    public OpenApiOAuthFlow? ClientCredentials { get; internal set; }

    /// <summary>The authorization code flow.</summary>
    public OpenApiOAuthFlow? AuthorizationCode { get; internal set; }
}

/// <summary>The OAuth Flow Object.</summary>
public sealed class OpenApiOAuthFlow : OpenApiElement
{
    /// <summary>The authorization URL.</summary>
    public string? AuthorizationUrl { get; internal set; }

    /// <summary>The token URL.</summary>
    public string? TokenUrl { get; internal set; }

    /// <summary>The refresh URL.</summary>
    public string? RefreshUrl { get; internal set; }

    /// <summary>Available scopes and their descriptions.</summary>
    public OrderedMap<string> Scopes { get; internal set; } = OrderedMap<string>.Empty;
}

/// <summary>
/// A Security Requirement Object: every listed scheme must be satisfied. An empty requirement means anonymous
/// access is allowed.
/// </summary>
public sealed class OpenApiSecurityRequirement : OpenApiElement
{
    /// <summary>Scheme names mapped to required scopes (or roles).</summary>
    public OrderedMap<IReadOnlyList<string>> Schemes { get; internal set; } = OrderedMap<IReadOnlyList<string>>.Empty;
}
