# Queries with dotnet-query

With `GenerateQueries=true`, BlazorApi generates a `{Api}Queries` class per client for
[dotnet-query](https://github.com/msynk/dotnet-query) (package `Msynk.DotNetQuery`), which provides caching,
deduplication, background refetching and mutations. BlazorApi does not implement a cache; it generates keys,
definitions and mutation options for dotnet-query.

> `Msynk.DotNetQuery` 0.8.0 is not on nuget.org yet. `Msynk.BlazorApi.DotNetQuery` depends on it, so add the feed
> that hosts it (or a local build of dotnet-query) before restoring. See [Troubleshooting](troubleshooting.md#msynkdotnetquery-cannot-be-restored).

## Setup

```xml
<PackageReference Include="Msynk.BlazorApi.DotNetQuery" Version="0.1.0-preview" />

<OpenApiContract Include="Contracts/petstore.yaml" Options="GenerateQueries=true" />
```

```csharp
builder.Services.AddDotNetQuery(options => options.DefaultStaleTime = TimeSpan.FromSeconds(30));
builder.Services.AddPetstoreClient(o => o.BaseAddress = new Uri("https://api.example.com/v1/"));
```

The class is named after the client without its `Client` suffix: `PetstoreClient` gives `PetstoreQueries`.

## Keys

Keys are hierarchical, so invalidating a prefix refreshes everything below it:

```csharp
PetstoreQueries.All                       // ["PetstoreClient"]: every query of this client
PetstoreQueries.Keys.GetPetPrefix         // ["PetstoreClient", "getPet"]: every getPet query
PetstoreQueries.Keys.GetPet(42)           // ["PetstoreClient", "getPet", 42]
PetstoreQueries.Keys.ListPets(limit: 20)  // every argument is part of the key, collections as arrays
```

Keys include every operation argument, including header parameters (for example a request id), so different header
values create different cache entries.

## Query definitions

`GET` operations with a response body (except binary downloads) get a `QueryDefinition<T>` factory:

```csharp
QueryDefinition<Pet> definition = PetstoreQueries.GetPet(api, petId: 42);
var result = await queryClient.FetchAsync(definition);   // cached by key, deduplicated
Pet? pet = result.Data;

using var observer = queryClient.Watch(definition);       // state changes for UI updates
await queryClient.InvalidateQueriesAsync(PetstoreQueries.Keys.GetPetPrefix);
```

Definitions call the throwing `XxxAsync` methods, so a failed query's error is an `ApiException`
(`ApiErrorView` can display it). Each factory takes an optional `QueryOptions`.

## Retry defaults

Without options, definitions use `ApiQueryDefaults` (namespace `BlazorApi.DotNetQuery`), which retries only transient
failures:

| Failure | Retried |
|---|---|
| `ApiNetworkException`, `ApiTimeoutException`, `HttpRequestException`, `TimeoutException` | yes |
| `ApiException` with status 408, 429 or 5xx | yes |
| `ApiDeserializationException`, other 4xx, other exceptions | no |

`ApiQueryDefaults.MaxRetries` is 3; `ShouldRetry(failureCount, error)` and `IsTransient(error)` are public.
`WithDefaults(options)` keeps your options and only adds `ShouldRetry` when you did not set one, so pass your own
`ShouldRetry` to use different rules.

## Mutations

Every other operation (not `GET` or `HEAD`) gets mutation options and a mutation factory:

```csharp
Mutation<NewPet, Pet> create = PetstoreQueries.CreatePet(queryClient, api);
Pet created = await create.MutateAsync(new NewPet { Name = "Rex", Kind = "dog" });

var update = PetstoreQueries.UpdatePet(queryClient, api);
await update.MutateAsync(new PetstoreQueries.UpdatePetVariables(42, pet));
```

- Variables: the single parameter's type; a generated `{Operation}Variables` record when there are several; `object?`
  when there are none. Operations without a response body return `true`.
- `{Operation}Options(options)` sets `MutationKey` (`All.Append(operationId)`) and `Invalidates` unless the given
  options set them; everything else in your `MutationOptions` is kept.

## Invalidation

After a mutation settles, dotnet-query invalidates the keys in `Invalidates`. The generated defaults are chosen in
this order:

1. **Configuration**: `invalidations` in the JSON configuration maps a mutation's operationId to query operationIds.
   ```json
   { "invalidations": { "placeOrder": ["getInventory"] } }
   ```
2. **`x-invalidates`** on the mutation operation in the contract:
   ```yaml
   /store/orders:
     post:
       operationId: placeOrder
       x-invalidates: [getInventory]
   ```
3. **Path relationships**: the queries whose path is an ancestor of, equal to, or a descendant of the mutation's
   path (path variables match regardless of their names). `POST /pets` and `PUT /pets/{petId}` invalidate
   `GET /pets` and `GET /pets/{petId}`; `POST /store/orders` invalidates nothing.

An explicit list (1 or 2) replaces the path rule; an empty list invalidates nothing. Invalidation uses each query's
prefix key, so all argument combinations are refreshed (it is not per argument). Only queries of the same client are
considered, which matters with `grouping=tag`. The `invalidations` setting exists only in the JSON configuration
(`Config` metadata for the source generator).

## Infinite queries

Paginated operations ([Pagination](pagination.md)) also get an `InfiniteQuery` factory that follows the API's
cursors, page numbers, offsets or `Link` headers:

```csharp
InfiniteQuery<string?, PetPage> pets = PetstoreQueries.ListPetsInfinite(queryClient, api, limit: 20);
await pets.FetchAsync();
if (pets.HasNextPage)
{
    await pets.FetchNextPageAsync();
}
IEnumerable<Pet> all = pets.CurrentState.Data!.Pages.SelectMany(page => page.Items);
```

The paging parameter is omitted from the signature. Infinite query keys are under the operation's prefix
(`Keys.ListPetsPrefix` plus `"infinite"` and the other arguments), so mutations that invalidate the operation also
refresh them. For `Link`-header pagination the next-page value is kept in a weak table keyed by page instance.

## Optimistic updates

Use dotnet-query's `MutationOptions.OptimisticUpdates`; the generated options keep them. The update is applied before
the mutation runs and rolled back if it fails; the generated `Invalidates` then reconciles with the server:

```csharp
var create = PetstoreQueries.CreatePet(queryClient, api, new MutationOptions<NewPet, Pet>
{
    OptimisticUpdates =
    [
        PetstoreQueries.ListPets(api, limit: 20).Optimistic<PetPage, NewPet>((page, added) => new PetPage
        {
            Items = [.. page?.Items ?? [], new Dog { Name = added.Name, Tag = added.Tag }],
            NextCursor = page?.NextCursor,
        }),
    ],
});
```

`OptimisticUpdate.Create<TVariables, TData>(key, update)` does the same with a key, for example
`PetstoreQueries.Keys.GetPet(42)`. Optimistic updates target an exact key; return a new instance rather than
modifying the cached one.

## Blazor

The dotnet-query Blazor adapter is not available as a package yet, so components subscribe to query state
themselves. `samples/Petstore.BlazorApp` shows the pattern (`Pages/Home.razor`, `Pages/Inventory.razor`); see
[Blazor](blazor.md#the-sample-app).
