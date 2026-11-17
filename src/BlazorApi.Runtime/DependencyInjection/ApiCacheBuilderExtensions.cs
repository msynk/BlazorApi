using BlazorApi.Runtime.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds conditional-request caching to a generated client's <see cref="IHttpClientBuilder"/>.</summary>
public static class ApiCacheBuilderExtensions
{
    /// <summary>
    /// Adds an <see cref="ApiCacheHandler"/>: GET responses with an <c>ETag</c> or <c>Last-Modified</c> are revalidated
    /// with <c>If-None-Match</c>/<c>If-Modified-Since</c>, and a 304 answer is served from memory. One cache is shared
    /// by the client's handler instances; entries are keyed by URL, credentials and <c>Vary</c> headers. Add it after
    /// authentication handlers so their credentials are part of the key.
    /// </summary>
    public static IHttpClientBuilder AddApiCaching(this IHttpClientBuilder builder, Action<ApiCacheOptions>? configure = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        var options = new ApiCacheOptions();
        configure?.Invoke(options);
        var cache = new ApiResponseCache(options);
        return builder.AddHttpMessageHandler(() => new ApiCacheHandler(cache));
    }
}
