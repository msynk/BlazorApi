using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Petstore.BlazorApp.Services;

/// <summary>
/// The base address of the Petstore API. The app calls the API it hosts itself, so unless the <c>PetstoreApi</c>
/// setting names another server, the address follows wherever Kestrel listens (<c>--urls</c>, launch profiles, ...).
/// </summary>
public sealed class PetstoreApiAddress(IServer server, IConfiguration configuration)
{
    private Uri? _value;

    public Uri Value => _value ??= Resolve();

    private Uri Resolve()
    {
        if (configuration["PetstoreApi"] is { Length: > 0 } configured)
        {
            return new Uri(configured.EndsWith('/') ? configured : configured + "/");
        }

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var listening = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The server is not listening yet; set PetstoreApi to the API's base address.");

        // Wildcard bindings (http://+:80, http://0.0.0.0:80, http://[::]:80) are not addresses a client can call.
        var builder = new UriBuilder(listening.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal));
        if (builder.Host is "0.0.0.0" or "[::]" or "::")
        {
            builder.Host = "localhost";
        }

        builder.Path = "/v1/";
        return builder.Uri;
    }
}
