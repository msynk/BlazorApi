using BlazorApi.Runtime;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers generated API clients with <c>IHttpClientFactory</c>.</summary>
public static class ApiClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a generated client as a typed <see cref="HttpClient"/> client. Generated <c>Add{Name}Client</c>
    /// methods call this; use the returned builder to add handlers (authentication, resilience, logging).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The HTTP client and options name.</param>
    /// <param name="factory">Creates the client from its HTTP client and options.</param>
    /// <param name="configure">Configures the client options.</param>
    public static IHttpClientBuilder AddApiClient<TClient>(
        this IServiceCollection services,
        string name,
        Func<HttpClient, ApiClientOptions, TClient> factory,
        Action<ApiClientOptions>? configure = null)
        where TClient : class
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        var options = services.AddOptions<ApiClientOptions>(name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddTransient<TClient>(sp => factory(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(name),
            sp.GetRequiredService<IOptionsMonitor<ApiClientOptions>>().Get(name)));
        return services.AddHttpClient(name);
    }
}
