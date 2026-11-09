using System.Net;
using System.Text.Json;
using BlazorApi.Integration.Tests.Infrastructure;
using BlazorApi.Runtime;
using BlazorApi.Runtime.Security;
using Microsoft.Extensions.DependencyInjection;
using Petstore.Client;

namespace BlazorApi.Integration.Tests;

public class PetstoreClientTests
{
    private const string DogJson = """{"id":42,"name":"Rex","kind":"dog","status":"available","tag":null,"birthDate":"2020-05-01","barkVolume":7.5,"owner":{"id":"7c9e6679-7425-40de-944b-e07fc1f90ae7","manager":{"id":"00000000-0000-0000-0000-000000000001"}}}""";

    private static (PetstoreClient Client, RecordingHandler Handler) Create(Action<ApiClientOptions>? configure = null)
    {
        var handler = new RecordingHandler();
        var options = new ApiClientOptions();
        configure?.Invoke(options);
        return (new PetstoreClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task Gets_a_polymorphic_pet_from_the_default_server()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, DogJson);

        var pet = await client.GetPetAsync(42);

        var dog = Assert.IsType<Dog>(pet);
        Assert.Equal("Rex", dog.Name);
        Assert.Equal("dog", dog.Kind);
        Assert.Equal(7.5, dog.BarkVolume);
        Assert.Equal(PetStatus.Available, dog.Status);
        Assert.Null(dog.Tag);
        Assert.Equal(new DateOnly(2020, 5, 1), dog.BirthDate);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), dog.Owner!.Manager!.Id);
        Assert.Equal("https://api.example.com/v1/pets/42", handler.Last.Uri.ToString());
        Assert.Equal(HttpMethod.Get, handler.Last.Method);
        Assert.Equal("application/json", handler.Last.Headers["Accept"]);
    }

    [Fact]
    public async Task Serializes_query_header_and_array_parameters()
    {
        var (client, handler) = Create(o => o.BaseAddress = new Uri("http://localhost:5000/api/"));
        handler.Respond(HttpStatusCode.OK, """{"items":[],"nextCursor":null}""");
        var requestId = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");

        var page = await client.ListPetsAsync(limit: 10, cursor: "a b&c", tags: ["x", "y z"], status: PetStatus.Pending, xRequestId: requestId);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.Equal("http://localhost:5000/api/pets?limit=10&cursor=a%20b%26c&tags=x&tags=y%20z&status=pending", handler.Last.Uri.AbsoluteUri);
        Assert.Equal(requestId.ToString(), handler.Last.Headers["X-Request-Id"]);
    }

    [Fact]
    public async Task Omits_unset_optional_parameters()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"items":[]}""");

        await client.ListPetsAsync();

        Assert.Equal("https://api.example.com/v1/pets", handler.Last.Uri.AbsoluteUri);
        Assert.False(handler.Last.Headers.ContainsKey("X-Request-Id"));
    }

    [Fact]
    public async Task Sends_json_bodies_without_unset_optional_properties()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.Created, """{"id":1,"name":"Tom","kind":"cat","indoor":true}""");

        var created = await client.CreatePetAsync(new NewPet { Name = "Tom", Kind = "cat" });

        Assert.True(Assert.IsType<Cat>(created).Indoor);
        Assert.Equal("application/json", handler.Last.ContentType);
        Assert.Equal("""{"name":"Tom","kind":"cat"}""", handler.Last.Body);
    }

    [Fact]
    public async Task Writes_derived_types_with_their_discriminator()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, DogJson);

        await client.UpdatePetAsync(42, new Dog { Name = "Rex", BarkVolume = 1 });

        using var json = JsonDocument.Parse(handler.Last.Body!);
        Assert.Equal("dog", json.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("barkVolume").GetDouble());
        Assert.False(json.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Unknown_discriminators_become_unrecognized_values()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"id":1,"name":"Polly","kind":"parrot","words":120}""");

        var pet = await client.GetPetAsync(1);

        var unknown = Assert.IsType<UnrecognizedPet>(pet);
        Assert.Equal("parrot", unknown.Kind);
        Assert.Equal(120, unknown.AdditionalProperties!["words"].GetInt32());
    }

    [Fact]
    public async Task Merge_patch_bodies_distinguish_absent_from_null()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, DogJson).Respond(HttpStatusCode.OK, DogJson);

        await client.PatchPetAsync(42, new PetPatch { Name = "Max" });
        var first = handler.Last.Body;
        await client.PatchPetAsync(42, new PetPatch { Tag = null });
        var second = handler.Last.Body;

        Assert.Equal("""{"name":"Max"}""", first);
        Assert.Equal("""{"tag":null}""", second);
        Assert.Equal("application/merge-patch+json", handler.Last.ContentType);
    }

    [Fact]
    public async Task Typed_error_bodies_are_exposed_by_results_and_exceptions()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.NotFound, """{"code":404,"message":"No such pet"}""")
            .Respond(HttpStatusCode.NotFound, """{"code":404,"message":"No such pet"}""");

        var result = await client.GetPetResultAsync(9);
        var exception = await Assert.ThrowsAsync<ApiException>(() => client.GetPetAsync(9));

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal(ApiErrorKind.Http, result.Error.Kind);
        Assert.True(result.Error.TryGetContent<Error>(out var error));
        Assert.Equal("No such pet", error.Message);
        Assert.Equal(404, exception.StatusCode);
        Assert.IsType<Error>(exception.Content);
        Assert.Contains("getPet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Problem_details_errors_are_typed()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.UnprocessableEntity, """{"title":"Invalid","status":422,"errors":{"name":["Required"]}}""", "application/problem+json");

        var result = await client.CreatePetResultAsync(new NewPet { Name = string.Empty, Kind = "dog" });

        var problem = Assert.IsType<Petstore.Client.ProblemDetails>(result.Error!.Content);
        Assert.Equal("Invalid", problem.Title);
        Assert.Equal(["Required"], problem.Errors!["name"]);
    }

    [Fact]
    public async Task Undeclared_error_status_keeps_the_raw_body()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.InternalServerError, "boom", "text/plain");

        var result = await client.UpdatePetResultAsync(1, new Dog { Name = "x" });

        Assert.Equal(500, result.StatusCode);
        Assert.Equal("boom", result.Error!.Body);
        Assert.Null(result.Error.Content);
    }

    [Fact]
    public async Task Unreadable_success_bodies_are_deserialization_errors()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"id":1,"kind":"dog"}""")
            .Respond(HttpStatusCode.OK, """{"name":"x","kind":"cat","status":"retired"}""");

        var missingRequired = await client.GetPetResultAsync(1);
        var unknownEnum = await Assert.ThrowsAsync<ApiDeserializationException>(() => client.GetPetAsync(1));

        Assert.Equal(ApiErrorKind.Deserialization, missingRequired.Error!.Kind);
        Assert.Contains("name", missingRequired.Error.Message, StringComparison.Ordinal);
        Assert.Contains("'retired' is not a valid PetStatus value", unknownEnum.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_failures_timeouts_and_cancellation_are_distinguished()
    {
        var (client, handler) = Create(o => o.Timeout = TimeSpan.FromMilliseconds(50));
        handler.Respond(_ => throw new HttpRequestException("connection refused"))
            .Respond(async (request, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var network = await Assert.ThrowsAsync<ApiNetworkException>(() => client.GetHealthAsync());
        var timeout = await client.GetHealthResultAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        handler.Respond(HttpStatusCode.OK, "ok", "text/plain");

        Assert.Contains("connection refused", network.Message, StringComparison.Ordinal);
        Assert.Equal(ApiErrorKind.Timeout, timeout.Error!.Kind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetHealthAsync(cancelled.Token));
    }

    [Fact]
    public async Task Text_and_empty_responses()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, "healthy", "text/plain").Respond(HttpStatusCode.NoContent);

        var health = await client.GetHealthAsync();
#pragma warning disable CS0618 // deletePet is deprecated by the contract.
        await client.DeletePetAsync(3);
#pragma warning restore CS0618

        Assert.Equal("healthy", health);
        Assert.Equal(HttpMethod.Delete, handler.Last.Method);
    }

    [Fact]
    public async Task Error_statuses_never_match_a_success_default_response()
    {
        var recording = new RecordingHandler();
        recording.Respond(HttpStatusCode.OK, "fine", "text/plain").Respond(HttpStatusCode.NotFound, "missing", "text/plain");
        var connection = new BlazorApi.Runtime.Http.ApiConnection(new HttpClient(recording), null, "https://example.test/");
        var handler = new BlazorApi.Runtime.Http.ApiResponseHandler<string>().Success("default", BlazorApi.Runtime.Http.ApiBody.ReadText);

        var ok = await connection.SendAsync(new BlazorApi.Runtime.Http.ApiRequestBuilder(HttpMethod.Get, "/a", "a"), handler, CancellationToken.None);
        var missing = await connection.SendAsync(new BlazorApi.Runtime.Http.ApiRequestBuilder(HttpMethod.Get, "/a", "a"), handler, CancellationToken.None);

        Assert.Equal("fine", ok.Value);
        Assert.False(missing.IsSuccess);
        Assert.Equal("missing", missing.Error!.Body);
    }

    [Fact]
    public async Task Empty_success_responses_have_no_value_instead_of_failing()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.NoContent).Respond(HttpStatusCode.OK, string.Empty);

        var noContent = await client.GetPetResultAsync(1);
        var emptyOk = await client.GetPetResultAsync(1);

        Assert.True(noContent.IsSuccess);
        Assert.Null(noContent.Value);
        Assert.True(emptyOk.IsSuccess);
        Assert.Null(emptyOk.Value);
    }

    [Fact]
    public async Task Uploads_multipart_and_downloads_files()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"url":"https://cdn.example.com/1.png","size":3}""")
            .Respond(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                response.Content.Headers.ContentType = new("image/png");
                response.Content.Headers.ContentDisposition = new("attachment") { FileName = "rex.png" };
                return Task.FromResult(response);
            });

        var info = await client.UploadPetPhotoAsync(1, FileContent.FromBytes([1, 2, 3], "rex.png", "image/png"), caption: "Rex");
        var upload = handler.Last;
        await using var download = await client.DownloadPetPhotoAsync(1);

        Assert.Equal(new Uri("https://cdn.example.com/1.png"), info.Url);
        Assert.Equal("multipart/form-data", upload.ContentType);
        Assert.Contains("name=file; filename=rex.png", upload.Body, StringComparison.Ordinal);
        Assert.Contains("name=caption", upload.Body, StringComparison.Ordinal);
        Assert.Contains("Rex", upload.Body, StringComparison.Ordinal);
        Assert.Equal("rex.png", download.FileName);
        Assert.Equal("image/png", download.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.ReadAllBytesAsync());
    }

    [Fact]
    public async Task Sends_form_bodies_from_models()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"petId":1,"quantity":2,"complete":true}""");

        await client.PlaceOrderAsync(new Order { PetId = 1, Quantity = 2, ShipDate = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), Complete = true });

        Assert.Equal("application/x-www-form-urlencoded", handler.Last.ContentType);
        Assert.Equal("petId=1&quantity=2&shipDate=2024-01-02T03%3A04%3A05Z&complete=true", handler.Last.Body);
    }

    [Fact]
    public async Task Reads_maps()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"available":3,"sold":1}""");

        var inventory = await client.GetInventoryAsync();

        Assert.Equal(3, inventory["available"]);
        Assert.Equal(1, inventory["sold"]);
    }

    [Fact]
    public async Task Authentication_handlers_follow_operation_security()
    {
        var recording = new RecordingHandler();
        recording.Respond(HttpStatusCode.OK, DogJson)
            .Respond(HttpStatusCode.OK, "ok", "text/plain")
            .Respond(HttpStatusCode.OK, "{}");
        var bearer = new BearerTokenHandler(new StaticAccessTokenProvider("secret-token"), "bearerAuth") { InnerHandler = recording };
        var apiKey = new ApiKeyHandler("X-API-Key", ApiKeyLocation.Header, _ => new ValueTask<string?>("key-1"), "apiKey") { InnerHandler = bearer };
        var client = new PetstoreClient(new HttpClient(apiKey));

        await client.GetPetAsync(1);
        var secured = recording.Last;
        await client.GetHealthAsync();
        var anonymous = recording.Last;
        await client.GetInventoryAsync();
        var keyed = recording.Last;

        Assert.Equal("Bearer secret-token", secured.Headers["Authorization"]);
        Assert.False(secured.Headers.ContainsKey("X-API-Key"));
        Assert.False(anonymous.Headers.ContainsKey("Authorization"));
        Assert.Equal("key-1", keyed.Headers["X-API-Key"]);
        Assert.False(keyed.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Bearer_handler_refreshes_once_after_401()
    {
        var recording = new RecordingHandler();
        recording.Respond(HttpStatusCode.Unauthorized).Respond(HttpStatusCode.OK, DogJson);
        var issued = 0;
        var provider = new CachingAccessTokenProvider((_, _) => new ValueTask<AccessToken?>(new AccessToken("token-" + ++issued)));
        var client = new PetstoreClient(new HttpClient(new BearerTokenHandler(provider) { InnerHandler = recording }));

        var pet = await client.GetPetAsync(1);

        Assert.IsType<Dog>(pet);
        Assert.Equal(2, issued);
        Assert.Equal("Bearer token-1", recording.Requests[0].Headers["Authorization"]);
        Assert.Equal("Bearer token-2", recording.Requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Registers_with_dependency_injection()
    {
        var handler = new RecordingHandler();
        handler.Respond(HttpStatusCode.OK, "ok", "text/plain");
        var services = new ServiceCollection();
        services.AddPetstoreClient(o => o.BaseAddress = new Uri("https://petstore.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IPetstoreClient>();
        await client.GetHealthAsync();

        Assert.Equal("https://petstore.test/health", handler.Last.Uri.AbsoluteUri);
    }

    [Fact]
    public void Models_round_trip_through_json()
    {
        var options = new ApiClientOptions().JsonSerializerOptions;
        Pet pet = new Cat { Name = "Tom", Indoor = true, Status = PetStatus.Sold, Attributes = new Dictionary<string, string> { ["color"] = "grey" } };

        var json = JsonSerializer.Serialize(pet, options);
        var back = JsonSerializer.Deserialize<Pet>(json, options);

        Assert.Equal("""{"indoor":true,"name":"Tom","kind":"cat","status":"sold","attributes":{"color":"grey"}}""", json);
        var cat = Assert.IsType<Cat>(back);
        Assert.Equal("grey", cat.Attributes!["color"]);
    }

    [Fact]
    public void Data_annotations_validate_constraints()
    {
        var pet = new NewPet { Name = "x", Kind = "dog" };
        var dog = new Dog { Name = new string('a', 101) };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(pet, new(pet), results, validateAllProperties: true);
        var invalid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(dog, new(dog), results, validateAllProperties: true);

        Assert.True(valid);
        Assert.False(invalid);
        Assert.Contains(results, r => r.MemberNames.Contains("Name"));
    }

    [Fact]
    public async Task Hooks_and_default_headers_apply_to_every_request()
    {
        var seen = new List<string>();
        var (client, handler) = Create(o =>
        {
            o.DefaultHeaders["User-Agent"] = "tests/1.0";
            o.OnRequest = (request, _) =>
            {
                seen.Add(request.Options.TryGetValue(BlazorApi.Runtime.Http.ApiRequestOptionKeys.OperationId, out var id) ? id : "?");
                return ValueTask.CompletedTask;
            };
        });
        handler.Respond(HttpStatusCode.OK, "ok", "text/plain");

        await client.GetHealthAsync();

        Assert.Equal(["getHealth"], seen);
        Assert.Equal("tests/1.0", handler.Last.Headers["User-Agent"]);
    }
}
