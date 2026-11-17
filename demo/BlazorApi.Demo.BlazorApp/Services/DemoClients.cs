using System.Globalization;
using BlazorApi.Runtime;
using Petstore.BlazorApp.Api;

namespace Petstore.BlazorApp.Services;

/// <summary>
/// Generated clients configured to fail on purpose, for the playground. Generated clients are plain classes, so besides
/// the one registered with <c>AddPetstoreClient</c> you can create them with any <see cref="HttpClient"/> and options.
/// </summary>
public sealed class DemoClients(IHttpClientFactory factory)
{
    public const string AnonymousClientName = "Petstore.Anonymous";

    /// <summary>A client without authentication handlers.</summary>
    public IPetstoreClient WithoutCredentials() => new PetstoreClient(factory.CreateClient(AnonymousClientName));

    /// <summary>A client pointed at a port nothing listens on.</summary>
    public IPetstoreClient Unreachable() => new PetstoreClient(
        factory.CreateClient(AnonymousClientName),
        new ApiClientOptions { BaseAddress = new Uri("http://127.0.0.1:9/v1/") });

    /// <summary>
    /// The authenticated client, with an <see cref="ApiClientOptions.OnRequest"/> callback that sees each request the
    /// generated code built (before the authentication handlers run).
    /// </summary>
    public IPetstoreClient Observed(Action<HttpRequestMessage> onRequest) => new PetstoreClient(
        factory.CreateClient("PetstoreClient"),
        new ApiClientOptions
        {
            OnRequest = (request, _) =>
            {
                onRequest(request);
                return ValueTask.CompletedTask;
            },
        });

    /// <summary>A client whose requests the API answers only after <paramref name="delay"/>.</summary>
    public IPetstoreClient Slow(TimeSpan delay, TimeSpan? timeout = null, bool authenticated = false)
    {
        var options = new ApiClientOptions { Timeout = timeout };
        options.DefaultHeaders[PetstoreEndpoints.DelayHeader] = ((int)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        return new PetstoreClient(factory.CreateClient(authenticated ? "PetstoreClient" : AnonymousClientName), options);
    }
}
