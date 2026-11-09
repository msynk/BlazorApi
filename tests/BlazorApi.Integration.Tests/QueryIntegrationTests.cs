using System.Net;
using BlazorApi.DotNetQuery;
using BlazorApi.Integration.Tests.Infrastructure;
using BlazorApi.Runtime;
using DotNetQuery;
using Petstore.Client;

namespace BlazorApi.Integration.Tests;

public class QueryIntegrationTests
{
    private const string Rex = """{"id":42,"name":"Rex","kind":"dog"}""";

    private static (PetstoreClient Client, RecordingHandler Handler, QueryClient Queries) Create()
    {
        var handler = new RecordingHandler();
        var client = new PetstoreClient(new HttpClient(handler));
        var queries = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5), DefaultRetryCount = 0 });
        return (client, handler, queries);
    }

    [Fact]
    public void Query_keys_are_stable_and_hierarchical()
    {
        Assert.Equal(PetstoreQueries.Keys.GetPet(1), PetstoreQueries.Keys.GetPet(1));
        Assert.NotEqual(PetstoreQueries.Keys.GetPet(1), PetstoreQueries.Keys.GetPet(2));
        Assert.True(PetstoreQueries.Keys.GetPetPrefix.IsPrefixOf(PetstoreQueries.Keys.GetPet(1)));
        Assert.True(PetstoreQueries.All.IsPrefixOf(PetstoreQueries.Keys.ListPetsPrefix));
        Assert.Equal(PetstoreQueries.Keys.ListPets(tags: ["a", "b"]), PetstoreQueries.Keys.ListPets(tags: new List<string> { "a", "b" }));
    }

    [Fact]
    public async Task Query_definitions_fetch_once_and_serve_from_cache()
    {
        var (client, handler, queries) = Create();
        handler.Respond(HttpStatusCode.OK, Rex);

        var first = await queries.FetchAsync(PetstoreQueries.GetPet(client, 42));
        var second = await queries.FetchAsync(PetstoreQueries.GetPet(client, 42));

        Assert.IsType<Dog>(first.Data);
        Assert.Same(first.Data, second.Data);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Mutations_invalidate_related_queries()
    {
        var (client, handler, queries) = Create();
        handler.Respond(HttpStatusCode.OK, Rex)
            .Respond(HttpStatusCode.OK, """{"id":42,"name":"Max","kind":"dog"}""")
            .Respond(HttpStatusCode.OK, """{"id":42,"name":"Max","kind":"dog"}""");
        await queries.FetchAsync(PetstoreQueries.GetPet(client, 42));

        var mutation = PetstoreQueries.UpdatePet(queries, client);
        await mutation.MutateAsync(new PetstoreQueries.UpdatePetVariables(42, new Dog { Name = "Max" }));
        var refreshed = await queries.FetchAsync(PetstoreQueries.GetPet(client, 42));

        Assert.Equal("Max", refreshed.Data!.Name);
        Assert.Equal([HttpMethod.Get, HttpMethod.Put, HttpMethod.Get], handler.Requests.Select(r => r.Method));
        Assert.Equal([PetstoreQueries.Keys.ListPetsPrefix, PetstoreQueries.Keys.GetPetPrefix], PetstoreQueries.UpdatePetOptions().Invalidates!);
        Assert.Empty(PetstoreQueries.PlaceOrderOptions().Invalidates!);
    }

    [Fact]
    public async Task Infinite_queries_follow_cursors()
    {
        var (client, handler, queries) = Create();
        handler.Respond(HttpStatusCode.OK, $$"""{"items":[{{Rex}}],"nextCursor":"c2"}""")
            .Respond(HttpStatusCode.OK, """{"items":[{"id":2,"name":"Tom","kind":"cat","indoor":true}],"nextCursor":null}""");

        using var infinite = PetstoreQueries.ListPetsInfinite(queries, client, limit: 1);
        await infinite.FetchAsync();
        Assert.True(infinite.HasNextPage);
        await infinite.FetchNextPageAsync();

        Assert.False(infinite.HasNextPage);
        Assert.Equal(2, infinite.CurrentState.PageCount);
        Assert.Equal("https://api.example.com/v1/pets?limit=1&cursor=c2", handler.Last.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Pagination_helpers_walk_every_page_and_item()
    {
        var (client, handler, _) = Create();
        handler.Respond(HttpStatusCode.OK, $$"""{"items":[{{Rex}},{{Rex}}],"nextCursor":"c2"}""")
            .Respond(HttpStatusCode.OK, $$"""{"items":[{{Rex}}],"nextCursor":""}""");

        var names = new List<string>();
        await foreach (var pet in client.ListPetsItemsAsync(limit: 2))
        {
            names.Add(pet.Name);
        }

        Assert.Equal(3, names.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("cursor=c2", handler.Last.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_retry_policy_retries_only_transient_failures()
    {
        static ApiException Http(int status) => Failure(status);

        Assert.True(ApiQueryDefaults.ShouldRetry(1, Http(503)));
        Assert.True(ApiQueryDefaults.ShouldRetry(1, Http(429)));
        Assert.False(ApiQueryDefaults.ShouldRetry(1, Http(404)));
        Assert.False(ApiQueryDefaults.ShouldRetry(1, Http(400)));
        Assert.False(ApiQueryDefaults.ShouldRetry(ApiQueryDefaults.MaxRetries + 1, Http(503)));
        Assert.True(ApiQueryDefaults.ShouldRetry(1, new HttpRequestException("reset")));
    }

    private static ApiException Failure(int status)
    {
        var handler = new RecordingHandler();
        handler.Respond((HttpStatusCode)status, "{}");
        var client = new PetstoreClient(new HttpClient(handler));
        return client.GetPetResultAsync(1).GetAwaiter().GetResult().Error!.ToException();
    }
}
