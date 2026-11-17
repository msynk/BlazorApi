using BlazorApi.Runtime.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds safe retries to a generated client's <see cref="IHttpClientBuilder"/>.</summary>
public static class ApiRetryBuilderExtensions
{
    /// <summary>
    /// Adds an <see cref="ApiRetryHandler"/>: idempotent requests (and requests with an idempotency key) are retried
    /// on transient status codes and connection failures, with exponential backoff and <c>Retry-After</c> support.
    /// Add it before authentication handlers so each retry gets a current token. Use either this or
    /// Microsoft.Extensions.Http.Resilience, not both.
    /// </summary>
    public static IHttpClientBuilder AddApiRetries(this IHttpClientBuilder builder, Action<ApiRetryOptions>? configure = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        var options = new ApiRetryOptions();
        configure?.Invoke(options);
        return builder.AddHttpMessageHandler(() => new ApiRetryHandler(options));
    }
}
