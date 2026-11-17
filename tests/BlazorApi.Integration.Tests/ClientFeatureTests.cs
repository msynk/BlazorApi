using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BlazorApi.Integration.Tests.Infrastructure;
using BlazorApi.Runtime;
using BlazorApi.Runtime.Diagnostics;
using BlazorApi.Runtime.Json;
using Features.Client;
using Petstore.Client;

namespace BlazorApi.Integration.Tests;

/// <summary>Per-call options, servers, streaming, forward-compatible enums, telemetry and source-generated JSON.</summary>
public class ClientFeatureTests
{
    private const string DogJson = """{"id":42,"name":"Rex","kind":"dog","status":"available","barkVolume":7.5}""";

    [Fact]
    public async Task WithOptions_applies_to_the_derived_client_only()
    {
        var handler = new RecordingHandler();
        handler.Respond(HttpStatusCode.OK, DogJson).Respond(HttpStatusCode.OK, DogJson);
        var client = new PetstoreClient(new HttpClient(handler));

        var derived = client.WithOptions(o =>
        {
            o.DefaultHeaders["If-None-Match"] = "\"v1\"";
            o.BaseAddress = new Uri("http://localhost:5000/api");
        });
        await derived.GetPetAsync(42);
        await client.GetPetAsync(42);

        Assert.Equal("\"v1\"", handler.Requests[0].Headers["If-None-Match"]);
        Assert.Equal("http://localhost:5000/api/pets/42", handler.Requests[0].Uri.AbsoluteUri);
        Assert.False(handler.Requests[1].Headers.ContainsKey("If-None-Match"));
        Assert.Equal("https://api.example.com/v1/pets/42", handler.Requests[1].Uri.AbsoluteUri);
        Assert.NotSame(client, derived);
    }

    [Fact]
    public void Options_clone_copies_collections()
    {
        var options = new ApiClientOptions { Timeout = TimeSpan.FromSeconds(3), MaxErrorBodyLength = 10 };
        options.DefaultHeaders["A"] = "1";
        options.OperationBaseAddresses["op"] = new Uri("https://x.example.com");

        var clone = options.Clone();
        clone.DefaultHeaders["B"] = "2";

        Assert.Equal(TimeSpan.FromSeconds(3), clone.Timeout);
        Assert.Equal(10, clone.MaxErrorBodyLength);
        Assert.Same(options.JsonSerializerOptions, clone.JsonSerializerOptions);
        Assert.Equal("1", clone.DefaultHeaders["A"]);
        Assert.False(options.DefaultHeaders.ContainsKey("B"));
        Assert.Equal(new Uri("https://x.example.com"), clone.OperationBaseAddresses["op"]);
    }

    [Fact]
    public async Task Operation_servers_win_over_the_base_address_unless_overridden()
    {
        var handler = new RecordingHandler();
        handler.Respond(HttpStatusCode.OK, "a", "text/plain").Respond(HttpStatusCode.OK, "b", "text/plain").Respond(HttpStatusCode.OK, "[]");
        var options = new ApiClientOptions { BaseAddress = new Uri("http://localhost:7000/") };
        var client = new Features31Client(new HttpClient(handler), options);

        await client.GetFileInfoAsync("x 1");
        await client.WithOptions(o => o.OperationBaseAddresses["getFileInfo"] = new Uri("http://localhost:7001/files/")).GetFileInfoAsync("y");
        await client.ListShapesAsync();

        Assert.Equal("https://files.example.com/files/x%201", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("http://localhost:7001/files/files/y", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("http://localhost:7000/shapes", handler.Requests[2].Uri.AbsoluteUri);
    }

    [Fact]
    public void Servers_resolve_variables()
    {
        var server = Assert.Single(Features31Client.Servers);

        Assert.Equal("Regional", server.Description);
        Assert.Equal(new Uri("https://eu.features.example.com/v2"), server.DefaultAddress);
        Assert.Equal(new Uri("https://us.features.example.com/v2"), server.Resolve(new Dictionary<string, string> { ["region"] = "us" }));
        Assert.Equal(["eu", "us"], server.AllowedValues("region"));
        Assert.Throws<ArgumentException>(() => server.Resolve(new Dictionary<string, string> { ["region"] = "mars" }));
        Assert.Throws<ArgumentException>(() => server.Resolve(new Dictionary<string, string> { ["planet"] = "earth" }));
        Assert.Equal("https://eu.features.example.com/v2", Features31Client.DefaultBaseAddress);
    }

    [Fact]
    public async Task Server_sent_events_stream_typed_items()
    {
        const string body = ": keep-alive\n\n" +
            "event: progress\nid: 1\nretry: 2500\ndata: {\"percent\":10,\n" +
            "data: \"stage\":\"queued\"}\n\n" +
            "data: {\"percent\":100,\"stage\":\"published\"}\n\n" +
            "event: lonely\n\n" +
            "data: [DONE]\n\n" +
            "data: {\"percent\":0,\"stage\":\"queued\"}"; // no blank line: an incomplete event is discarded
        var handler = new RecordingHandler().Respond(HttpStatusCode.OK, body, "text/event-stream");
        var client = new Features31Client(new HttpClient(handler));

        await using var stream = await client.StreamEventsAsync();
        var events = await stream.ToListAsync();

        Assert.Equal("text/event-stream", handler.Last.Headers["Accept"]);
        Assert.Equal(3, events.Count);
        Assert.Equal("progress", events[0].EventType);
        Assert.Equal("1", events[0].Id);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), events[0].Retry);
        Assert.Equal("{\"percent\":10,\n\"stage\":\"queued\"}", events[0].Data);
        Assert.Equal(10, events[0].GetValue().Percent);
        Assert.Equal(Stage.Queued, events[0].GetValue().Stage);
        Assert.Equal("message", events[1].EventType);
        Assert.Equal("1", events[1].Id); // the last event id persists
        Assert.Equal(Stage.Unrecognized, events[1].GetValue().Stage); // forward-compatible enum
        Assert.Equal("[DONE]", events[2].Data);
        Assert.False(events[2].TryGetValue(out _));
    }

    [Fact]
    public async Task Json_lines_and_json_seq_stream_typed_items()
    {
        var handler = new RecordingHandler()
            .Respond(HttpStatusCode.OK, "{\"percent\":1,\"stage\":\"queued\"}\r\n\n{\"percent\":2,\"stage\":\"running\"}\n", "application/jsonl")
            .Respond(HttpStatusCode.OK, "\u001e{\"percent\":3,\n \"stage\":\"done\"}\n\u001e{\"percent\":4,\"stage\":\"done\"}\n", "application/jsonl");
        var client = new Features31Client(new HttpClient(handler));

        var lines = await (await client.ExportShapesAsync()).ToListAsync();
        var records = await (await client.ExportShapesAsync()).ToListAsync();

        Assert.Equal([1, 2], lines.Select(p => p.Percent));
        Assert.Equal(Stage.Running, lines[1].Stage);
        Assert.Equal([3, 4], records.Select(p => p.Percent));
    }

    [Fact]
    public async Task Stream_items_that_do_not_match_fail_during_enumeration()
    {
        var handler = new RecordingHandler().Respond(HttpStatusCode.OK, "{\"percent\":1,\"stage\":\"queued\"}\n{\"percent\":\"x\"}\n", "application/jsonl");
        var client = new Features31Client(new HttpClient(handler));

        var result = await client.ExportShapesResultAsync();
        Assert.True(result.IsSuccess);
        var stream = result.Value!;
        var seen = new List<int>();
        var ex = await Assert.ThrowsAsync<ApiDeserializationException>(async () =>
        {
            await foreach (var item in stream)
            {
                seen.Add(item.Percent);
            }
        });

        Assert.Equal([1], seen);
        Assert.Equal("exportShapes", ex.Error.OperationId);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.ToListAsync());
    }

    [Fact]
    public async Task Streams_can_be_created_for_mocks()
    {
        var events = ApiStream.From([ServerSentEvent<Progress>.FromValue(new Progress { Percent = 5, Stage = Stage.Done })]);
        var items = await events.ToListAsync();

        Assert.Equal(5, Assert.Single(items).GetValue().Percent);
        Assert.Contains("\"percent\":5", items[0].Data, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrecognized_enum_values_cannot_be_sent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StageJsonConverter.ToWireValue(Stage.Unrecognized));
        Assert.Equal("\"done\"", JsonSerializer.Serialize(Stage.Done, ApiJson.CreateOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSerializer.Serialize(Stage.Unrecognized, ApiJson.CreateOptions()));
    }

    [Fact]
    public async Task Source_generated_metadata_serves_requests_without_reflection()
    {
        // Only the generated context and the runtime's own metadata: any type missing from them fails.
        var json = new JsonSerializerOptions(ApiJson.CreateOptions())
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(PetstoreJsonContext.Default),
        };
        var handler = new RecordingHandler()
            .Respond(HttpStatusCode.OK, DogJson)
            .Respond(HttpStatusCode.Created, DogJson)
            .Respond(HttpStatusCode.OK, DogJson)
            .Respond(HttpStatusCode.OK, """{"items":[{"id":1,"name":"Tom","kind":"cat","indoor":true}],"nextCursor":null}""")
            .Respond(HttpStatusCode.OK, """{"dogs":3}""");
        var client = new PetstoreClient(new HttpClient(handler), new ApiClientOptions { JsonSerializerOptions = json });

        var dog = Assert.IsType<Dog>(await client.GetPetAsync(42));
        await client.CreatePetAsync(new NewPet { Name = "Rex", Kind = "dog" });
        await client.PatchPetAsync(42, new PetPatch { Tag = null });
        var page = await client.ListPetsAsync(tags: ["a"]);
        var inventory = await client.GetInventoryAsync();

        Assert.Equal(7.5, dog.BarkVolume);
        Assert.Contains("\"name\":\"Rex\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal("""{"tag":null}""", handler.Requests[2].Body);
        Assert.IsType<Cat>(Assert.Single(page.Items));
        Assert.Equal(3, inventory["dogs"]);
    }

    [Fact]
    public async Task Default_options_of_a_client_with_a_json_context_use_it()
    {
        var handler = new RecordingHandler().Respond(HttpStatusCode.OK, DogJson);
        var client = new PetstoreClient(new HttpClient(handler));

        await client.GetPetAsync(1);

        var resolver = client.Connection.JsonOptions.TypeInfoResolver!;
        Assert.NotNull(resolver.GetTypeInfo(typeof(Pet), client.Connection.JsonOptions));
        Assert.Contains(PetstoreJsonContext.Default, client.Connection.JsonOptions.TypeInfoResolverChain);
    }

    [Fact]
    public async Task Operations_are_traced_and_measured()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ApiTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (activities)
                {
                    activities.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var measurements = new List<(double Value, Dictionary<string, object?> Tags)>();
        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ApiTelemetry.Name)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            lock (measurements)
            {
                measurements.Add((value, copy));
            }
        });
        meterListener.Start();

        string? parentInHandler = null;
        var handler = new RecordingHandler()
            .Respond(request =>
            {
                parentInHandler ??= Activity.Current?.OperationName;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DogJson, Encoding.UTF8, "application/json") });
            })
            .Respond(HttpStatusCode.NotFound, """{"code":404,"message":"no"}""");
        var client = new PetstoreClient(new HttpClient(handler));

        using (var parent = new Activity("telemetry-test").Start())
        {
            await client.GetPetAsync(42);
            await client.GetPetResultAsync(43);
        }

        List<Activity> mine;
        lock (activities)
        {
            mine = activities.Where(a => a.OperationName == "getPet" && a.Parent?.OperationName == "telemetry-test").ToList();
        }

        Assert.Equal(2, mine.Count);
        Assert.Equal("getPet", parentInHandler);
        Assert.Equal("/pets/{petId}", mine[0].GetTagItem("url.template"));
        Assert.Equal("GET", mine[0].GetTagItem("http.request.method"));
        Assert.Equal(200, mine[0].GetTagItem("http.response.status_code"));
        Assert.Equal(ActivityStatusCode.Unset, mine[0].Status);
        Assert.Equal("404", mine[1].GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, mine[1].Status);
        Assert.DoesNotContain(mine.SelectMany(a => a.TagObjects), t => t.Value is string s && s.Contains("43", StringComparison.Ordinal));
        lock (measurements)
        {
            Assert.Contains(measurements, m => Equals(m.Tags["blazorapi.operation.id"], "getPet") && Equals(m.Tags.GetValueOrDefault("error.type"), "404"));
        }
    }

    [Fact]
    public async Task Resilience_rejections_become_results_instead_of_escaping()
    {
        var handler = new RecordingHandler()
            .Respond(_ => throw new Polly.Timeout.TimeoutRejectedException())
            .Respond(_ => throw new Polly.CircuitBreaker.BrokenCircuitException());
        var client = new PetstoreClient(new HttpClient(handler));

        var timedOut = await client.GetPetResultAsync(1);
        var rejected = await client.GetPetResultAsync(2);

        Assert.Equal(ApiErrorKind.Timeout, timedOut.Error!.Kind);
        Assert.Equal(ApiErrorKind.Network, rejected.Error!.Kind);
        Assert.IsType<Polly.CircuitBreaker.BrokenCircuitException>(rejected.Error.Exception);
    }
}

