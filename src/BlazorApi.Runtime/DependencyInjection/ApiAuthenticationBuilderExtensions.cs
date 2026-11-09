using BlazorApi.Runtime.Security;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds authentication handlers to a generated client's <see cref="IHttpClientBuilder"/>.</summary>
public static class ApiAuthenticationBuilderExtensions
{
    /// <summary>
    /// Adds bearer token authentication for operations that require <paramref name="scheme"/> (every request when
    /// null). The provider is resolved per handler; tokens stay in memory only.
    /// </summary>
    public static IHttpClientBuilder AddBearerToken(this IHttpClientBuilder builder, Func<IServiceProvider, IAccessTokenProvider> provider, string? scheme = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        return builder.AddHttpMessageHandler(services => new BearerTokenHandler(provider(services), scheme));
    }

    /// <summary>
    /// Adds bearer token authentication from a token callback, for example a Blazor WebAssembly
    /// <c>IAccessTokenProvider.RequestAccessToken</c> call. Tokens are cached in memory until shortly before they expire.
    /// </summary>
    public static IHttpClientBuilder AddBearerToken(
        this IHttpClientBuilder builder,
        Func<IServiceProvider, IReadOnlyList<string>, CancellationToken, ValueTask<AccessToken?>> getToken,
        string? scheme = null)
    {
        if (getToken is null)
        {
            throw new ArgumentNullException(nameof(getToken));
        }

        return builder.AddBearerToken(services => new CachingAccessTokenProvider((scopes, cancellationToken) => getToken(services, scopes, cancellationToken)), scheme);
    }

    /// <summary>Adds an API key for operations that require <paramref name="scheme"/> (every request when null).</summary>
    public static IHttpClientBuilder AddApiKey(
        this IHttpClientBuilder builder,
        string name,
        ApiKeyLocation location,
        Func<IServiceProvider, CancellationToken, ValueTask<string?>> key,
        string? scheme = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        return builder.AddHttpMessageHandler(services => new ApiKeyHandler(name, location, cancellationToken => key(services, cancellationToken), scheme));
    }

    /// <summary>Adds HTTP Basic authentication for operations that require <paramref name="scheme"/> (every request when null).</summary>
    public static IHttpClientBuilder AddBasicAuthentication(
        this IHttpClientBuilder builder,
        Func<IServiceProvider, CancellationToken, ValueTask<(string UserName, string Password)?>> credentials,
        string? scheme = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (credentials is null)
        {
            throw new ArgumentNullException(nameof(credentials));
        }

        return builder.AddHttpMessageHandler(services => new BasicAuthenticationHandler(cancellationToken => credentials(services, cancellationToken), scheme));
    }
}
