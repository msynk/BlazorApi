using System.Net;
using System.Text;
using BlazorApi.Integration.Tests.Infrastructure;
using BlazorApi.Runtime;
using BlazorApi.Runtime.Http;
using Microsoft.Extensions.DependencyInjection;
using Petstore.Client;

namespace BlazorApi.Integration.Tests;

public class RetryHandlerTests
{
    private const string DogJson = """{"id":42,"name":"Rex","kind":"dog","status":"available"}""";

    private static (PetstoreClient Client, RecordingHandler Handler) Create(Action<ApiRetryOptions>? configure = null)
    {
        var handler = new RecordingHandler();
        var options = new ApiRetryOptions { BaseDelay = TimeSpan.Zero };
        configure?.Invoke(options);
        var retry = new ApiRetryHandler(options) { InnerHandler = handler };
        return (new PetstoreClient(new HttpClient(retry)), handler);
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Status(HttpStatusCode status, string? retryAfter = null) => _ =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return Task.FromResult(response);
    };

    [Fact]
    public async Task Idempotent_requests_are_retried_on_transient_statuses()
    {
        var (client, handler) = Create();
        handler.Respond(Status(HttpStatusCode.ServiceUnavailable, "0"))
            .Respond(Status(HttpStatusCode.TooManyRequests))
            .Respond(HttpStatusCode.OK, DogJson);

        var pet = await client.GetPetAsync(42);

        Assert.Equal("Rex", pet.Name);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_stop_after_the_limit_and_return_the_last_response()
    {
        var (client, handler) = Create(o => o.MaxRetries = 1);
        handler.Respond(Status(HttpStatusCode.BadGateway)).Respond(Status(HttpStatusCode.BadGateway));

        var result = await client.GetPetResultAsync(42);

        Assert.Equal(502, result.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Non_idempotent_requests_are_not_retried_without_an_idempotency_key()
    {
        var (client, handler) = Create();
        handler.Respond(Status(HttpStatusCode.ServiceUnavailable));

        var result = await client.CreatePetResultAsync(new NewPet { Name = "Rex", Kind = "dog" });

        Assert.Equal(503, result.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Generated_idempotency_keys_make_posts_retryable_with_the_same_key()
    {
        var (client, handler) = Create(o => o.AddIdempotencyKeys = true);
        handler.Respond(Status(HttpStatusCode.InternalServerError)).Respond(HttpStatusCode.Created, DogJson);

        await client.CreatePetAsync(new NewPet { Name = "Rex", Kind = "dog" });

        Assert.Equal(2, handler.Requests.Count);
        var key = handler.Requests[0].Headers["Idempotency-Key"];
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(key, handler.Requests[1].Headers["Idempotency-Key"]);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public async Task Caller_supplied_idempotency_keys_make_posts_retryable()
    {
        var (client, handler) = Create();
        handler.Respond(Status(HttpStatusCode.ServiceUnavailable)).Respond(HttpStatusCode.Created, DogJson);

        await client.WithOptions(o => o.DefaultHeaders["Idempotency-Key"] = "k-1").CreatePetAsync(new NewPet { Name = "Rex", Kind = "dog" });

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("k-1", handler.Requests[1].Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task Retry_after_beyond_the_limit_is_not_waited_for()
    {
        var (client, handler) = Create(o => o.MaxRetryAfter = TimeSpan.FromSeconds(1));
        handler.Respond(Status(HttpStatusCode.ServiceUnavailable, "120"));

        var result = await client.GetPetResultAsync(42);

        Assert.Equal(503, result.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Connection_failures_are_retried_and_other_statuses_are_not()
    {
        var (client, handler) = Create();
        handler.Respond(_ => throw new HttpRequestException("reset"))
            .Respond(Status(HttpStatusCode.NotFound));

        var result = await client.GetPetResultAsync(42);

        Assert.Equal(404, result.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Streamed_uploads_are_buffered_so_they_can_be_resent()
    {
        var (client, handler) = Create();
        handler.Respond(Status(HttpStatusCode.ServiceUnavailable)).Respond(HttpStatusCode.OK, """{"url":"https://x/p.png","size":3}""");
        using var photo = new MemoryStream([1, 2, 3]);

        await client.WithOptions(o => o.DefaultHeaders["Idempotency-Key"] = "upload-1")
            .UploadPetPhotoAsync(42, new FileContent(photo, "p.png", "image/png"), "hi");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public async Task Registers_with_the_client_builder()
    {
        var handler = new RecordingHandler();
        handler.Respond(Status(HttpStatusCode.ServiceUnavailable, "0")).Respond(HttpStatusCode.OK, DogJson);
        var services = new ServiceCollection();
        services.AddPetstoreClient()
            .AddApiRetries(o => o.BaseDelay = TimeSpan.Zero)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IPetstoreClient>().GetPetAsync(42);

        Assert.Equal(2, handler.Requests.Count);
    }
}
