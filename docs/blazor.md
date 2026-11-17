# Blazor

Generated clients work in Blazor Server, Blazor WebAssembly and Blazor Web Apps like in any .NET app. The optional
package `BlazorApi.Blazor` adds components for the common UI states. Add the namespaces to `_Imports.razor`:

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

`ApiResultView` does not cache: every load calls the API.

## ApiErrorView

Shows an `ApiError` (or any exception, using `ApiException.Error` when it is one): a title, a detail, field errors
from problem details, and an optional retry button. It renders nothing when there is no error.

```razor
<ApiErrorView Error="_error" />
<ApiErrorView Exception="_exception" OnRetry="LoadAsync" RetryText="Reload" CssClass="alert" />
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

## Generated types in component parameters

The Razor compiler and the BlazorApi source generator run side by side, so when Razor reads a component's
declaration it does not see the types the generator produces. A parameter typed with a generated model then fails to
compile at its call sites (`CS0400: The type or namespace name 'Pet' could not be found in the global namespace`).
Write generated types fully qualified, and not as nullable reference types, in `[Parameter]` declarations:

```razor
@code {
    [Parameter, EditorRequired]
    public global::MyApp.Api.Pet Pet { get; set; } = null!;

    [Parameter]
    public EventCallback<global::MyApp.Api.Pet> OnSaved { get; set; }
}
```

Everything else (`@code` bodies, `ApiResultView TValue="Pet"`, expressions in markup) can use the short names. Value
types such as generated enums (`PetStatus?`) are not affected. With the CLI (`blazorapi generate`) the code is
ordinary source and this does not apply.

## The demo app

`demo/BlazorApi.Demo.BlazorApp` is a Blazor Web App (interactive server rendering) that hosts a small in-memory
implementation of every operation in `demo/Contracts/petstore.yaml` under `/v1`, and consumes it through the
client generated from that contract.

```shell
dotnet run --project demo/BlazorApi.Demo.BlazorApp     # http://localhost:5080
```

The app calls the API it hosts, at whatever address Kestrel listens on (`--urls` works); set `PetstoreApi` to call
another server. An **API traffic** panel lists every request the generated client sends: the operation id, the URL
it built and which credentials the handlers attached (secrets are never shown). Every demo page has a
**How this page works** panel with the key code and links to the full files.

The app also documents the library from the inside:

- **Overview** (`/`): what the generator produces from the contract, with live counts, and a map of the demo.
- **Guide** (`/docs/...`): getting started, calling operations, handling errors, Blazor components, authentication,
  pagination, models and testing. Each page has highlighted code and live examples that call the hosted API (for
  example the request a `ListPetsResultAsync` call builds, `ApiResultView` in four states, or JSON deserialized into
  the polymorphic `Pet`). Tables of operations, error bodies, security requirements and model properties are read
  from the contract and the compiled models, so they stay accurate.
- **Contract & code** (`/code`): the contract's operations with their generated method signatures, the contract
  itself (editable), the generated files, and the generator's diagnostics. It runs `ApiClientGenerator.Generate`
  in the app (which is why the demo references `BlazorApi.CodeGeneration`; applications do not need it), so
  changing the generator options or the contract shows the resulting code immediately.
- **Source browser** (`/source`): every file of the demo projects, embedded at build time, which the guide links to.

- `Program.cs` registers the generated client with `AddPetstoreClient`, a bearer token for the `bearerAuth` scheme,
  an API key for `apiKey` and a logging `DelegatingHandler` added last, so it sees the headers the others attached.
  `/health` stays anonymous because the contract says `security: []`.
- **Pets** (`Pages/PetList.razor`, `Pets/NewPetForm.razor`): cursor pagination with `ListPetsResultAsync` and
  "Load more", a typed `status` filter, the total from the `X-Total-Count` response header, and a create form with
  `DataAnnotationsValidator`, `ApiValidationErrors` and `CreatePetResultAsync`. An empty name or the "Parrot" kind
  shows the server's 422 problem details on the fields.
- **Pet details** (`Pages/PetDetails.razor`): `ApiResultView` with custom `Loading` and `Error` templates (a missing
  pet reads the typed 404 `Error` body with `TryGetContent`), and one card per kind of request body: a JSON merge
  patch whose preview shows how `Optional<string?>` omits or nulls the tag (`Pets/PetPatchEditor.razor`), a PUT with a
  polymorphic `Pet` body, a multipart photo upload and binary download (`Pets/PetPhotoCard.razor`), a form-urlencoded
  order, and the deprecated (`[Obsolete]`) delete.
- **Inventory** (`Pages/Inventory.razor`): `ApiResultView` around `GetInventoryResultAsync` (an
  `additionalProperties` object, generated as `IReadOnlyDictionary<string, int>`), refreshed with `ReloadAsync()`
  and `KeepPreviousValue`; the API key is added to this operation only.
- **Error playground** (`Pages/Playground.razor`): runs each failure and shows what comes back: a typed 404 body,
  422 problem details, a 401 from a client without credentials, `ApiException` from the throwing methods, and
  `ApiErrorKind.Network`, `ApiErrorKind.Timeout` and caller cancellation (`Services/DemoClients.cs` builds the
  failing clients).
- `Api/PetstoreEndpoints.cs` and `Api/PetStore.cs`: the in-memory API, which uses the generated models for its own
  JSON. Data resets when the app restarts.

`demo/BlazorApi.Demo.ConsoleClient` calls the same API without dependency injection (start the Blazor app first), and
`demo/BlazorApi.Demo.Tests` shows mocks, fixtures, `MockApiHandler` and a compatibility test.
