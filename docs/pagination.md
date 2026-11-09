# Pagination

BlazorApi detects paginated `GET` operations from naming conventions, or from an explicit `x-pagination` extension,
and generates helpers that walk every page and item. `api-gen inspect` shows the result (`[paged:cursor]`).

## Detection

Detection looks at the operation's query parameters and the first success response with a JSON body (the "page").
Names are compared case-insensitively. The first matching style wins:

| Style | Requires |
|---|---|
| Cursor | a string query parameter named `cursor`, `after`, `pageToken`, `page_token`, `nextPageToken`, `continuationToken`, `continuation`, `startingAfter`, `nextToken` or `next` (and snake_case forms), **and** a string page property named `nextCursor`, `nextPageToken`, `continuationToken`, `nextToken`, `endCursor`, `cursor` or `next` (and snake_case forms) |
| Link header | the response declares a `Link` header, and there is an integer page parameter or a cursor parameter |
| Page | an integer query parameter named `page`, `pageNumber`, `page_number`, `pageIndex`, `page_index` or `pageNo` |
| Offset | an integer query parameter named `offset`, `skip` or `start` |

Then, for the detected style:

- **Page size**: an integer parameter named `pageSize`, `page_size`, `per_page`, `perPage`, `limit`, `size`, `take`,
  `count`, `max_results`, `maxResults` or `top`.
- **Items**: the page itself when it is an array; otherwise an array property named `items`, `data`, `results`,
  `values`, `records`, `content`, `entries`, `elements`, `list`, `rows` or `nodes`, or the only array property.
- **Has more**: a boolean `hasMore`, `has_more`, `hasNext`, `has_next`, `hasNextPage`, `has_next_page` or `more`.
- **Total**: an integer `total`, `totalCount`, `totalItems`, `totalElements` or `count` (and snake_case forms).
- **Total pages** (page style): an integer `totalPages`, `pageCount` or `pages` (and snake_case forms).

Page properties are searched at the top level, then inside a container property named `meta`, `pagination`,
`paging`, `page`, `pageInfo`, `page_info`, `links` or `_meta` (for example `meta.nextCursor`).

## x-pagination

Declare pagination explicitly when the names do not follow these conventions. The extension replaces detection for
that operation:

```yaml
/orders:
  get:
    operationId: listOrders
    x-pagination:
      style: cursor            # cursor, page, offset, link (or linkHeader)
      parameter: continue      # the query parameter that selects the page (required)
      size: max                # the page size query parameter
      next: paging.token       # property path of the next cursor (required for cursor)
      items: entries           # property path of the items array
      hasMore: paging.more     # property path of a "has more" flag
      total: paging.total      # property path of the total item count
      totalPages: paging.pages # property path of the total page count
```

Style values are case-sensitive. Paths are dotted property names (JSON names) from the page object. An invalid declaration (missing `style` or
`parameter`, a parameter that is not a query parameter of the operation, or a cursor `next` path that does not exist)
reports BAPI1009 and the operation is not treated as paginated.

## Page and item walkers

For each paginated operation whose paging parameter is optional, the `{Client}Pagination` class adds extension
methods on the client interface (they also work with mocks):

```csharp
// Every page; the paging parameter is managed for you and omitted from the signature.
await foreach (PetPage page in api.ListPetsPagesAsync(limit: 100, status: PetStatus.Available))
{
    Console.WriteLine($"{page.Items.Count} pets");
}

// Every item of every page (generated when the items array is known).
await foreach (Pet pet in api.ListPetsItemsAsync(limit: 100, cancellationToken: ct))
{
    Console.WriteLine(pet.Name);
}
```

The walkers call the throwing `XxxAsync` methods (`XxxResultAsync` for `Link` headers, then `GetValueOrThrow()`), so
a failed page throws an `ApiException`. They stop when:

| Style | Start | Stops when |
|---|---|---|
| Cursor | no cursor | the next cursor is empty or unchanged, or "has more" is false |
| Page | the parameter's numeric default, else 1 | a page has no items, "has more" is false, the total page count is reached, or fewer items than the requested page size arrive |
| Offset | 0 (advances by the item count) | a page has no items, "has more" is false, the total is reached, or fewer items than the requested page size arrive |
| Link header | no value | there is no `rel="next"` link with the paging parameter, or its value is unchanged |

Walkers are not generated when they could not stop safely: cursor style without a next-cursor property, page style
without items, "has more" or total pages, and offset style without items. Required paging parameters also disable
them.

## Infinite queries

With `GenerateQueries=true`, the same operations get an `{Operation}Infinite` factory that returns a dotnet-query
`InfiniteQuery<TPageParam, TPage>` using the same next-page rules. See [Queries](queries.md#infinite-queries).

## Link headers

`ApiLinkHeader` (namespace `BlazorApi.Runtime.Http`) reads RFC 8288 `Link` headers from any result, here for a
hypothetical `listRepos` operation:

```csharp
ApiResult<IReadOnlyList<Repo>> result = await api.ListReposResultAsync(page: 1);
string? nextUrl = ApiLinkHeader.GetLink(result.Headers, "next");
string? nextPage = ApiLinkHeader.GetQueryParameter(result.Headers, "page");   // relation "next" by default
```
