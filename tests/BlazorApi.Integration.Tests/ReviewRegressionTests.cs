using System.Net;

using BlazorApi.Integration.Tests.Infrastructure;
using BlazorApi.Runtime;
using BlazorApi.Runtime.Http;
using BlazorApi.Runtime.Json;
using Features.Client;
using Petstore.Client;

namespace BlazorApi.Integration.Tests;

/// <summary>Regressions for the issues found by the code reviews of the runtime.</summary>
public class ReviewRegressionTests
{


    [Theory]
    [InlineData("Infinity")]
    [InlineData("1e300")]
    public async Task Absurd_retry_after_values_return_the_response(string value)
    {
        var handler = new RecordingHandler().Respond(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            response.Headers.TryAddWithoutValidation("retry-after-ms", value);
            return Task.FromResult(response);
        });
        var client = new PetstoreClient(new HttpClient(new ApiRetryHandler { InnerHandler = handler }));

        var result = await client.GetPetResultAsync(1);

        Assert.Equal(503, result.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Event_streams_skip_a_byte_order_mark_and_ignore_out_of_range_retry()
    {
        var body = "﻿retry: 99999999999999999\ndata: {\"percent\":1,\"stage\":\"queued\"}\n\n";
        var handler = new RecordingHandler().Respond(HttpStatusCode.OK, body, "text/event-stream");
        var client = new Features31Client(new HttpClient(handler));

        var events = await (await client.StreamEventsAsync()).ToListAsync();

        var e = Assert.Single(events);
        Assert.Null(e.Retry);
        Assert.Equal(1, e.GetValue().Percent);
    }

    [Theory]
    [InlineData("P99999999999999999999999999D")]
    [InlineData("PT99999999999999999999999999H")]
    [InlineData("P20000000D")]
    public void Durations_beyond_time_span_are_rejected(string text) =>
        Assert.False(ApiDuration.TryParse(text, out _));

    [Fact]
    public async Task Unlimited_error_bodies_are_read()
    {
        var handler = new RecordingHandler().Respond(HttpStatusCode.NotFound, """{"code":404,"message":"gone"}""");
        var client = new PetstoreClient(new HttpClient(handler), new ApiClientOptions { MaxErrorBodyLength = int.MaxValue });

        var result = await client.GetPetResultAsync(1);

        Assert.Equal("""{"code":404,"message":"gone"}""", result.Error!.Body);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\boot.ini", "boot.ini")]
    [InlineData("photo.png", "photo.png")]
    [InlineData("..", null)]
    public async Task Download_file_names_lose_directory_segments(string name, string? expected)
    {
        var handler = new RecordingHandler().Respond(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) };
            response.Content.Headers.TryAddWithoutValidation("Content-Disposition", $"attachment; filename=\"{name}\"");
            return Task.FromResult(response);
        });
        var client = new PetstoreClient(new HttpClient(handler));

        await using var file = await client.DownloadPetPhotoAsync(1);

        Assert.Equal(expected, file.FileName);
    }

    [Theory]
    [InlineData("evil.com#")]
    [InlineData("evil.com/x")]
    [InlineData("a@evil.com")]
    public void Server_variables_cannot_change_the_host(string value)
    {
        var server = new ApiServer("https://{tenant}.example.com/v1", defaults: new Dictionary<string, string> { ["tenant"] = "app" });

        Assert.Throws<ArgumentException>(() => server.Resolve(new Dictionary<string, string> { ["tenant"] = value }));
        Assert.Equal(new Uri("https://eu-1.example.com/v1"), server.Resolve(new Dictionary<string, string> { ["tenant"] = "eu-1" }));
    }

    [Fact]
    public void Server_path_variables_cannot_add_queries_or_climb()
    {
        var server = new ApiServer("https://api.example.com/{basePath}", defaults: new Dictionary<string, string> { ["basePath"] = "v1" });

        Assert.Equal(new Uri("https://api.example.com/api/v2"), server.Resolve(new Dictionary<string, string> { ["basePath"] = "api/v2" }));
        Assert.Throws<ArgumentException>(() => server.Resolve(new Dictionary<string, string> { ["basePath"] = "v1?x=1" }));
        Assert.Throws<ArgumentException>(() => server.Resolve(new Dictionary<string, string> { ["basePath"] = "../admin" }));
    }

    [Fact]
    public async Task Asynchronous_test_streams_are_disposed_asynchronously()
    {
        var disposed = false;
        async IAsyncEnumerable<int> Items()
        {
            try
            {
                yield return 1;
                await Task.Yield();
                yield return 2;
            }
            finally
            {
                await Task.Yield();
                disposed = true;
            }
        }

        var stream = ApiStream.From(Items());
        await foreach (var item in stream)
        {
            if (item == 1)
            {
                break;
            }
        }

        Assert.True(disposed);
    }

    [Fact]
    public async Task Lines_longer_than_the_limit_fail_the_stream()
    {
        var line = new string('x', LineReaderLimit() + 10);
        var handler = new RecordingHandler().Respond(HttpStatusCode.OK, line, "application/jsonl");
        var client = new Features31Client(new HttpClient(handler));

        var stream = await client.ExportShapesAsync();

        await Assert.ThrowsAsync<ApiDeserializationException>(() => stream.ToListAsync());
    }

    private static int LineReaderLimit() => 16 * 1024 * 1024;
}
