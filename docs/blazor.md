# Blazor

Generated clients work in Blazor Server, Blazor WebAssembly and Blazor Web Apps like in any .NET app. The optional
package `Msynk.BlazorApi.Blazor` adds components for the common UI states. Add the namespaces to `_Imports.razor`:

```razor
@using BlazorApi.Blazor
@using BlazorApi.Runtime
```

## ApiResultView

Loads a value with a generated `...ResultAsync` method and renders the loading, success or error state. It reloads
when `Key` changes and cancels a pending load when the key changes again or the component is disposed.

```razor
<ApiResultView TValue="Pet" Load="ct => Api.GetPetResultAsync(PetId, ct)" Key="PetId">
    <ChildContent Context="pet">
        <h1>@pet.Name</h1>
        @if (pet is Dog dog) { <p>Bark volume: @dog.BarkVolume</p> }
    </ChildContent>
    <Loading><p>Loading the pet...</p></Loading>
</ApiResultView>
```

| Parameter / member | Meaning |
|---|---|
| `Load` (required) | `Func<CancellationToken, Task<ApiResult<TValue>>>` |
| `Key` | Identifies what is loaded; a new value triggers a reload. |
| `ChildContent` | `RenderFragment<TValue>` rendered on success. |
| `Loading` | Rendered while loading. Default: `<div class="api-loading" aria-busy="true">Loading...</div>`. |
| `Error` | `RenderFragment<ApiError>` rendered on failure. Default: an `ApiErrorView` with a retry button. |
| `KeepPreviousValue` | Keep showing the previous value while reloading. |
| `Result`, `IsLoading`, `ReloadAsync()` | The latest result, the loading flag, and a manual reload (use `@ref`). |

`ApiResultView` does not cache. For caching, deduplication and background refetching use the generated dotnet-query
definitions ([Queries](queries.md)).

## ApiErrorView

Shows an `ApiError` (or any exception, using `ApiException.Error` when it is one): a title, a detail, field errors
from problem details, and an optional retry button. It renders nothing when there is no error.

```razor
<ApiErrorView Error="_error" />
<ApiErrorView Exception="state.Error" OnRetry="() => query.RefetchAsync()" RetryText="Reload" CssClass="alert" />
```

The title and detail come from `ApiValidation.Describe(error)`: problem details `title`/`detail`, else the JSON
body's `title`/`message`/`error` and `detail`/`error_description`, else a message for the error kind or status code
(for example "You need to sign in." for 401, "The service could not be reached." for network errors). The markup is
a `div` with `role="alert"` (class `api-error` by default) containing `strong`, `p`, `ul` and `button` elements.
Server text is always rendered as text, never as markup.

## Server validation in EditForm

`ApiValidationErrors` shows the field errors of a failed call next to the matching inputs. Combined with the
DataAnnotations generated from the contract, the same form shows client-side and server-side errors:

```razor
<EditForm Model="_new" OnValidSubmit="CreateAsync">
    <DataAnnotationsValidator />
    <ApiValidationErrors Error="_createError" />
    <ValidationSummary />

    <InputText @bind-Value="_new.Name" />
    <ValidationMessage For="() => _new.Name" />
    <button type="submit">Add</button>
</EditForm>

@code {
    private NewPet _new = new() { Name = "", Kind = "dog" };
    private ApiError? _createError;

    private async Task CreateAsync()
    {
        var result = await Api.CreatePetResultAsync(_new);
        _createError = result.Error;   // null on success clears the messages
    }
}
```

- Field errors are read from the `errors` member of a problem details body (`{"errors": {"name": ["Required"]}}`),
  whether the body was typed as `ApiProblemDetails`, as the contract's own model, or is only available as text.
- Keys map to model properties case-insensitively and accept `name`, `Name`, `$.name` and dotted paths
  (`owner.email`). Unknown keys become form-level errors shown by `ValidationSummary`.
- A field's server messages are cleared when the user edits it, and all of them when the form is validated again.
- It must be placed inside an `EditForm`. `ApiValidationErrors.Apply(editContext, store, error)` and
  `ApiValidationErrors.FieldFor(model, key)` are available for custom validators; `ApiValidation.GetFieldErrors(error)`
  returns the raw field errors.

## Authentication

Register handlers on the generated client ([Authentication](authentication.md)). In Blazor WebAssembly, forward
tokens from `IAccessTokenProvider.RequestAccessToken` with the callback overload of `AddBearerToken`
([example](authentication.md#blazor-webassembly)).

In Blazor Server, `IHttpClientFactory` creates message handlers in their own dependency injection scope, not in the
user's circuit scope. Do not resolve per-user services (such as `AuthenticationStateProvider`) inside a handler
factory; obtain per-user tokens through a service designed for that.

## The sample app

`samples/Petstore.BlazorApp` is a Blazor Web App (interactive server rendering) that hosts a small in-memory Petstore
API under `/v1` and consumes it through the client generated from `samples/Contracts/petstore.yaml`.

```shell
dotnet run --project samples/Petstore.BlazorApp     # http://localhost:5080
```

It needs `Msynk.DotNetQuery` to restore ([Troubleshooting](troubleshooting.md#msynkdotnetquery-cannot-be-restored)).

- `Program.cs` registers dotnet-query (`AddDotNetQuery`), the generated client with `AddPetstoreClient`, a bearer
  token for the `bearerAuth` scheme and an API key for `apiKey`. `/health` stays anonymous because the contract says
  `security: []`.
- `Pages/Home.razor`: an infinite query (`PetstoreQueries.ListPetsInfinite`) with "Load more"; a create form with
  `DataAnnotationsValidator`, `ApiValidationErrors` and the `CreatePet` mutation, which invalidates the list.
  Submitting an empty name or the "Parrot" kind shows the server's 422 problem details on the fields.
- `Pages/PetDetails.razor`: `ApiResultView` around `GetPetResultAsync` (a missing pet shows the API's 404 body), a
  JSON merge-patch update (an empty tag sends `"tag": null`), and delete with cache invalidation.
- `Pages/Inventory.razor`: a cached query watched with `Queries.Watch(...)` and refreshed by invalidating
  `PetstoreQueries.Keys.GetInventoryPrefix`; the API key is added only to this operation.
- `Api/PetstoreEndpoints.cs`: the in-memory API, which uses the generated models for its own JSON.

`samples/Petstore.ConsoleClient` calls the same API without dependency injection (start the Blazor app first), and
`samples/Petstore.Tests` shows mocks, fixtures, `MockApiHandler` and a compatibility test.
