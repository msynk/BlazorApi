# Authentication

Authentication is done by `DelegatingHandler`s in the client's `HttpClient` pipeline. The runtime
(`Msynk.BlazorApi.Runtime`, namespace `BlazorApi.Runtime.Security`) provides the handlers; extension methods on
`IHttpClientBuilder` (namespace `Microsoft.Extensions.DependencyInjection`) add them to a generated client.

## Scheme-aware handlers

Generated clients attach each operation's security requirements to the request
(`ApiRequestOptionKeys.Security`). A handler registered with a `scheme` name applies only to operations whose
requirements name that scheme from `components.securitySchemes`:

```yaml
security: [{ bearerAuth: [] }]          # document default
paths:
  /store/inventory:
    get: { security: [{ apiKey: [] }] }  # API key only
  /health:
    get: { security: [] }                # anonymous
```

```csharp
builder.Services
    .AddPetstoreClient(o => o.BaseAddress = new Uri("https://api.example.com/v1/"))
    .AddBearerToken(_ => new StaticAccessTokenProvider("token"), scheme: "bearerAuth")
    .AddApiKey("X-API-Key", ApiKeyLocation.Header, (_, _) => ValueTask.FromResult<string?>("key"), scheme: "apiKey");
```

Here `/health` gets nothing, `/store/inventory` gets only the API key, and every other operation gets the bearer
token. Rules:

- With `scheme: null` (the default) a handler applies to every request, except operations that declare
  `security: []`.
- Requests that do not come from a generated client (no security metadata) always get the credential.
- When an operation lists alternatives (`[{a: []}, {b: []}]`), every registered handler whose scheme is listed
  applies.
- The scopes of the matching requirement are passed to the token provider.
- Bearer and Basic handlers do not overwrite an `Authorization` header that is already set.

## Bearer tokens

`AddBearerToken` has two overloads:

```csharp
// 1. Your own IAccessTokenProvider (resolved when the handler is created).
.AddBearerToken(services => services.GetRequiredService<MyTokenProvider>(), scheme: "bearerAuth")

// 2. A token callback; the tokens are cached in memory until shortly before they expire.
.AddBearerToken(async (services, scopes, cancellationToken) =>
{
    var tokens = services.GetRequiredService<ITokenService>();       // your service
    var (value, expiresAt) = await tokens.GetAsync(scopes, cancellationToken);
    return new AccessToken(value, expiresAt);
}, scheme: "bearerAuth")
```

`IAccessTokenProvider` has two members: `GetTokenAsync(scopes, cancellationToken)` (return `null` to send the request
without a token) and `InvalidateAsync(token, cancellationToken)`.

**Refresh on 401.** When a request with a token gets `401 Unauthorized`, `BearerTokenHandler` calls
`InvalidateAsync`; if the provider returns `true` and then supplies a different token, the request is sent once more.
Requests are retried only when their body can be replayed (no body, string, byte array, form or JSON content;
multipart and stream bodies are not retried). `StaticAccessTokenProvider` never refreshes.

**Caching.** `CachingAccessTokenProvider` wraps a token source, caches one token per scope set, and refreshes it
`refreshBefore` (default 60 seconds) before `AccessToken.ExpiresAt`. Concurrent callers share a single refresh. The
callback overload of `AddBearerToken` creates one cache per handler instance; `IHttpClientFactory` recycles handlers
(every two minutes by default), so register a provider as a singleton when tokens are expensive.

## OAuth 2.0 client credentials

```csharp
builder.Services.AddHttpClient("token-endpoint");
builder.Services.AddSingleton(sp => new CachingAccessTokenProvider(
    new ClientCredentialsTokenSource(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("token-endpoint"),
        new Uri("https://login.example.com/oauth2/token"),
        clientId: builder.Configuration["Api:ClientId"]!,
        clientSecret: builder.Configuration["Api:ClientSecret"]!).GetTokenAsync));

builder.Services
    .AddPetstoreClient()
    .AddBearerToken(sp => sp.GetRequiredService<CachingAccessTokenProvider>(), scheme: "bearerAuth");
```

`ClientCredentialsTokenSource` posts `grant_type=client_credentials` with the client id, secret and requested scopes,
and reads `access_token` and `expires_in`. A rejected request throws `HttpRequestException` with the status code
only; the response body is not included because it may echo secrets.

## API keys and Basic authentication

```csharp
.AddApiKey("X-API-Key", ApiKeyLocation.Header,
    (services, ct) => ValueTask.FromResult(services.GetRequiredService<IConfiguration>()["Api:Key"]), scheme: "apiKey")

.AddBasicAuthentication((services, ct) =>
    ValueTask.FromResult<(string UserName, string Password)?>(("user", "secret")), scheme: "basicAuth")
```

`ApiKeyLocation` is `Header`, `Query` or `Cookie`. A `null` key or credential sends the request without one.

Without dependency injection, chain the handlers yourself:

```csharp
var handler = new BearerTokenHandler(new StaticAccessTokenProvider("token"), "bearerAuth")
{
    InnerHandler = new HttpClientHandler(),
};
var api = new PetstoreClient(new HttpClient(handler), new ApiClientOptions { BaseAddress = baseAddress });
```

## Blazor WebAssembly

The following is an example, not code shipped by BlazorApi. It forwards tokens from the WebAssembly authentication
library (`Microsoft.AspNetCore.Components.WebAssembly.Authentication`), whose `IAccessTokenProvider` has the same
name as BlazorApi's, hence the alias:

```csharp
using BlazorApi.Runtime.Security;
using Wasm = Microsoft.AspNetCore.Components.WebAssembly.Authentication;

builder.Services
    .AddPetstoreClient(o => o.BaseAddress = new Uri("https://api.example.com/v1/"))
    .AddBearerToken(async (services, scopes, cancellationToken) =>
    {
        var provider = services.GetRequiredService<Wasm.IAccessTokenProvider>();
        var result = await provider.RequestAccessToken(new Wasm.AccessTokenRequestOptions
        {
            Scopes = scopes.Count > 0 ? scopes : null,   // null: the provider's default scopes
        });
        return result.TryGetToken(out var token) ? new AccessToken(token.Value, token.Expires) : null;
    }, scheme: "bearerAuth");
```

When no token is available the request is sent without one; handle the resulting 401 (for example with
`ApiErrorView`, or by redirecting to sign-in). See [Blazor](blazor.md#authentication) for Blazor Server notes.

## Security notes

- Tokens, keys and passwords stay in memory. `CachingAccessTokenProvider` keeps tokens in a dictionary only; nothing
  is persisted.
- Credentials are never logged or included in messages: `AccessToken.ToString()` returns `AccessToken(****)`,
  `ApiError.Message` never contains credentials, and the client credentials source does not echo error bodies.
- API keys sent in the query string can end up in server and proxy logs; prefer a header when the API allows it.

See [Security](security.md) for the full threat model.
