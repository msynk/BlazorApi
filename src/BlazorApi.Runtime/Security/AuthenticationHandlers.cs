using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BlazorApi.Runtime.Http;

namespace BlazorApi.Runtime.Security;

/// <summary>
/// Adds <c>Authorization: Bearer</c> tokens to requests whose operation requires the configured security scheme
/// (any request when the operation metadata is absent). On a 401 response it invalidates the token and retries
/// once when the request can be replayed.
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly IAccessTokenProvider _provider;
    private readonly string? _scheme;

    /// <summary>Creates the handler.</summary>
    /// <param name="provider">The token provider.</param>
    /// <param name="scheme">The OpenAPI security scheme name this handler serves; <see langword="null"/> for all requests.</param>
    public BearerTokenHandler(IAccessTokenProvider provider, string? scheme = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _scheme = scheme;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!SecurityScheme.Applies(request, _scheme, out var scopes) || request.Headers.Authorization is not null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var token = await _provider.GetTokenAsync(scopes, cancellationToken).ConfigureAwait(false);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || token is null || !CanReplay(request))
        {
            return response;
        }

        if (!await _provider.InvalidateAsync(token, cancellationToken).ConfigureAwait(false))
        {
            return response;
        }

        var refreshed = await _provider.GetTokenAsync(scopes, cancellationToken).ConfigureAwait(false);
        if (refreshed is null || refreshed.Value == token.Value)
        {
            return response;
        }

        response.Dispose();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.Value);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool CanReplay(HttpRequestMessage request) =>
        request.Content is null or StringContent or ByteArrayContent or FormUrlEncodedContent ||
        request.Content.GetType().Name == "JsonContent";
}

/// <summary>Where an API key is sent.</summary>
public enum ApiKeyLocation
{
    /// <summary>A request header.</summary>
    Header,

    /// <summary>A query parameter.</summary>
    Query,

    /// <summary>A cookie.</summary>
    Cookie,
}

/// <summary>Adds an API key to requests whose operation requires the configured security scheme.</summary>
public sealed class ApiKeyHandler : DelegatingHandler
{
    private readonly string _name;
    private readonly ApiKeyLocation _location;
    private readonly Func<CancellationToken, ValueTask<string?>> _key;
    private readonly string? _scheme;

    /// <summary>Creates the handler.</summary>
    /// <param name="name">The header, query parameter or cookie name.</param>
    /// <param name="location">Where the key goes.</param>
    /// <param name="key">Provides the key; a <see langword="null"/> key sends the request without one.</param>
    /// <param name="scheme">The OpenAPI security scheme name; <see langword="null"/> for all requests.</param>
    public ApiKeyHandler(string name, ApiKeyLocation location, Func<CancellationToken, ValueTask<string?>> key, string? scheme = null)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _location = location;
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _scheme = scheme;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (SecurityScheme.Applies(request, _scheme, out _) && await _key(cancellationToken).ConfigureAwait(false) is { } key)
        {
            switch (_location)
            {
                case ApiKeyLocation.Header:
                    request.Headers.Remove(_name);
                    request.Headers.TryAddWithoutValidation(_name, key);
                    break;
                case ApiKeyLocation.Query when request.RequestUri is not null:
                    var builder = new UriBuilder(request.RequestUri);
                    var pair = Uri.EscapeDataString(_name) + "=" + Uri.EscapeDataString(key);
                    builder.Query = string.IsNullOrEmpty(builder.Query) ? pair : builder.Query.TrimStart('?') + "&" + pair;
                    request.RequestUri = builder.Uri;
                    break;
                case ApiKeyLocation.Cookie:
                    var cookie = _name + "=" + Uri.EscapeDataString(key);
                    var existing = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
                    request.Headers.Remove("Cookie");
                    request.Headers.TryAddWithoutValidation("Cookie", existing is null ? cookie : existing + "; " + cookie);
                    break;
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Adds HTTP Basic credentials to requests whose operation requires the configured security scheme.</summary>
public sealed class BasicAuthenticationHandler : DelegatingHandler
{
    private readonly Func<CancellationToken, ValueTask<(string UserName, string Password)?>> _credentials;
    private readonly string? _scheme;

    /// <summary>Creates the handler.</summary>
    public BasicAuthenticationHandler(Func<CancellationToken, ValueTask<(string UserName, string Password)?>> credentials, string? scheme = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _scheme = scheme;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (SecurityScheme.Applies(request, _scheme, out _) && request.Headers.Authorization is null &&
            await _credentials(cancellationToken).ConfigureAwait(false) is { } credentials)
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

internal static class SecurityScheme
{
    public static bool Applies(HttpRequestMessage request, string? scheme, out IReadOnlyList<string> scopes)
    {
        if (scheme is null)
        {
            scopes = Array.Empty<string>();
            return !request.Options.TryGetValue(ApiRequestOptionKeys.Security, out var requirements) || requirements.Count > 0;
        }

        return ApiSecurityRequirement.Applies(request, scheme, out scopes);
    }
}
