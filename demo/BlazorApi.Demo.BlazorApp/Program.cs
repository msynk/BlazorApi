using BlazorApi.Runtime.Security;
using Microsoft.AspNetCore.Diagnostics;
using BlazorApi.Demo.BlazorApp.Api;
using BlazorApi.Demo.BlazorApp.Components;
using BlazorApi.Demo.BlazorApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Serve the framework's static files (blazor.web.js) from the build output in every environment, not only in
// Development, so `dotnet run --no-launch-profile` works too. Published apps are not affected.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// The in-memory API this app hosts (see Api/PetstoreEndpoints.cs) and the services of the demo UI.
builder.Services.AddSingleton<PetStore>();
builder.Services.AddSingleton<PetstoreApiAddress>();
builder.Services.AddSingleton<ApiActivityLog>();
builder.Services.AddTransient<ApiActivityHandler>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<DemoClients>();

// The in-app guide: the demo's own source files and the generator run at runtime for the code explorer.
builder.Services.AddSingleton<DemoSources>();
builder.Services.AddSingleton<ContractExplorer>();

// The generated client, registered with IHttpClientFactory. Each authentication handler applies only to the operations
// whose security requirements name its scheme: health stays anonymous, the inventory gets the API key and everything
// else gets the bearer token. The activity handler is added last, so it sees the headers the others attached.
builder.Services
    .AddPetstoreClient(options => options.Timeout = TimeSpan.FromSeconds(15))
    .ConfigureHttpClient((services, http) => http.BaseAddress = services.GetRequiredService<PetstoreApiAddress>().Value)
    .AddBearerToken(_ => new StaticAccessTokenProvider(PetStore.DemoToken), scheme: "bearerAuth")
    .AddApiKey("X-API-Key", ApiKeyLocation.Header, (_, _) => new ValueTask<string?>(PetStore.DemoApiKey), scheme: "apiKey")
    .AddHttpMessageHandler<ApiActivityHandler>();

// A client without credentials, used by the playground to show what the API answers then.
builder.Services
    .AddHttpClient(DemoClients.AnonymousClientName, (services, http) => http.BaseAddress = services.GetRequiredService<PetstoreApiAddress>().Value)
    .AddHttpMessageHandler<ApiActivityHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

// Unknown pages render the app's "not found" page; API responses keep their own bodies.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1") && context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
    {
        statusCodePages.Enabled = false;
    }

    return next(context);
});

app.UseAntiforgery();
app.MapStaticAssets();
app.MapPetstoreApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
