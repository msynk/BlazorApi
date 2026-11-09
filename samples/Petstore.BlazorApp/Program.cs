using BlazorApi.Runtime.Security;
using Petstore.BlazorApp.Api;
using Petstore.BlazorApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<PetStore>();

// dotnet-query holds the cache; the generated PetstoreQueries supply keys, definitions and mutations.
builder.Services.AddDotNetQuery(options => options.DefaultStaleTime = TimeSpan.FromSeconds(30));

// The generated client, registered with IHttpClientFactory. Each handler applies only to the operations whose
// security requirements name its scheme: health stays anonymous, inventory gets the API key, pets get the token.
builder.Services
    .AddPetstoreClient(options => options.BaseAddress = new Uri(builder.Configuration["PetstoreApi"] ?? "http://localhost:5080/v1/"))
    .AddBearerToken(_ => new StaticAccessTokenProvider(PetStore.DemoToken), scheme: "bearerAuth")
    .AddApiKey("X-API-Key", ApiKeyLocation.Header, (_, _) => new ValueTask<string?>(PetStore.DemoApiKey), scheme: "apiKey");

var app = builder.Build();

app.UseAntiforgery();
app.MapPetstoreApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
