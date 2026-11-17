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

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Logging_records_operations_without_urls_and_warns_about_deprecation_once()
    {
        var logger = new ListLogger();
        var handler = new RecordingHandler()
            .Respond(HttpStatusCode.OK, DogJson)
            .Respond(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
                response.Headers.TryAddWithoutValidation("Deprecation", "@1700000000");
                response.Headers.TryAddWithoutValidation("Sunset", "Sun, 01 Jun 2031 00:00:00 GMT");
                response.Headers.TryAddWithoutValidation("Link", "<https://docs.example.com/v2>; rel=\"deprecation\"");
                return Task.FromResult(response);
            })
            .Respond(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DogJson, Encoding.UTF8, "application/json") };
                response.Headers.TryAddWithoutValidation("Deprecation", "@1700000000");
                return Task.FromResult(response);
            })
            .Respond(_ => throw new HttpRequestException("reset"));
        var client = new PetstoreClient(new HttpClient(new ApiLoggingHandler(logger) { InnerHandler = handler }));

        await client.GetPetResultAsync(42);
        await client.GetPetResultAsync(4242);
        await client.GetPetResultAsync(424242);
        await client.GetPetResultAsync(1);

        var messages = logger.Entries.Select(e => e.Message).ToList();
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Debug && e.Message.StartsWith("getPet (GET /pets/{petId}) responded 200", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information && e.Message.Contains("responded 404", StringComparison.Ordinal));
        var deprecated = Assert.Single(logger.Entries, e => e.Message.Contains("deprecated", StringComparison.Ordinal));
        Assert.Contains("Sunset: Sun, 01 Jun 2031", deprecated.Message, StringComparison.Ordinal);
        Assert.Contains("see https://docs.example.com/v2", deprecated.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("failed after", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("4242", StringComparison.Ordinal) || m.Contains("api.example.com", StringComparison.Ordinal));
    }
}
