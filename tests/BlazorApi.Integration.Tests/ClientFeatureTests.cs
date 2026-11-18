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
    public async Task Generated_metadata_covers_unions_hierarchies_streams_and_alternative_bodies_without_reflection()
    {
        // Only the generated context: a missing type would throw NotSupportedException.
        var json = new JsonSerializerOptions(ApiJson.CreateOptions())
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(Features31JsonContext.Default),
        };
        var handler = new RecordingHandler()
            .Respond(HttpStatusCode.OK, """[{"shapeType":"Circle","radius":2},{"shapeType":"Square","side":3},{"shapeType":"Hexagon","n":6}]""")
            .Respond(HttpStatusCode.OK, """{"vehicleType":"SportsCar","seats":2,"topSpeed":300}""")
            .Respond(HttpStatusCode.OK, """{"vehicleType":"Car","seats":4,"color":"red"}""")
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.Accepted, """{"percent":5,"stage":"queued"}""")
            .Respond(HttpStatusCode.OK, "data: {\"percent\":7,\"stage\":\"running\"}\n\n", "text/event-stream")
            .Respond(HttpStatusCode.OK, "{\"percent\":9,\"stage\":\"later\"}\n", "application/jsonl");
        var client = new Features31Client(new HttpClient(handler), new ApiClientOptions { JsonSerializerOptions = json });

        var shapes = await client.ListShapesAsync();
        var sports = await client.GetCarAsync("a");
        var car = await client.GetCarAsync("b");
        await client.PutCarAsync("b", car);
        var pending = await client.GetDocumentResultAsync("d");
        var events = await (await client.StreamEventsAsync()).ToListAsync();
        var lines = await (await client.ExportShapesAsync()).ToListAsync();

        Assert.IsType<Circle>(shapes[0]);
        Assert.IsType<Square>(shapes[1]);
        Assert.IsType<UnrecognizedShape>(shapes[2]);
        Assert.Equal(300, Assert.IsType<SportsCar>(sports).TopSpeed);
        Assert.Equal(typeof(Car), car.GetType());
        Assert.Contains("\"seats\":4", handler.Requests[3].Body, StringComparison.Ordinal);
        Assert.Equal(5, GetDocumentResponseBodies.From(pending).Accepted!.Percent);
        Assert.Equal(Stage.Running, events.Single().GetValue().Stage);
        Assert.Equal(Stage.Unrecognized, lines.Single().Stage);
    }

    [Fact]
    public void Generated_metadata_matches_reflection_for_options_and_property_order()
    {
        static JsonSerializerOptions Options(IJsonTypeInfoResolver resolver, Action<JsonSerializerOptions>? configure = null)
        {
            var options = new JsonSerializerOptions(ApiJson.CreateOptions()) { TypeInfoResolver = resolver };
            configure?.Invoke(options);
            return options;
        }

        IJsonTypeInfoResolver[] resolvers = [new DefaultJsonTypeInfoResolver(), JsonTypeInfoResolver.Combine(Features31JsonContext.Default)];
        foreach (var resolver in resolvers)
        {
            // Number handling from the options applies to properties, as with reflection.
            var lenient = Options(resolver, o => o.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString);
            Assert.Equal(5, JsonSerializer.Deserialize<Progress>("""{"percent":"5","stage":"queued"}""", lenient)!.Percent);

            // A class's own properties come before its base's.
            var car = new SportsCar { Seats = 2, TopSpeed = 300 };
            Assert.Equal("""{"topSpeed":300,"seats":2,"vehicleType":"SportsCar"}""", JsonSerializer.Serialize(car, Options(resolver)));

#if NET9_0_OR_GREATER
            // Non-nullable declarations are enforced when nullable annotations are respected.
            var strict = Options(resolver, o => o.RespectNullableAnnotations = true);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Document>("""{"id":null,"title":"t","summary":null}""", strict));
            Assert.Null(JsonSerializer.Deserialize<Document>("""{"id":"a","title":"t","summary":null}""", strict)!.Summary);
#endif
        }
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

    [Fact]
    public async Task OpenApi_3_2_methods_and_query_strings_are_sent()
    {
        var handler = new RecordingHandler()
            .Respond(HttpStatusCode.OK, "[]")
            .Respond(HttpStatusCode.NoContent);
        var client = new Features31Client(new HttpClient(handler), new ApiClientOptions { BaseAddress = new Uri("http://localhost/") });

        await client.SearchDocumentsAsync(new SearchDocumentsFilter { Text = "a b", Limit = 5 });
        await client.CopyDocumentAsync("d1", "/documents/d2");

        Assert.Equal("QUERY", handler.Requests[0].Method.Method);
        Assert.Equal("http://localhost/search?text=a%20b&limit=5", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("COPY", handler.Requests[1].Method.Method);
        Assert.Equal("/documents/d2", handler.Requests[1].Headers["Destination"]);
    }

    [Theory]
    [InlineData("PT0S", 0)]
    [InlineData("PT1H30M", 5400)]
    [InlineData("P1DT2H", 93600)]
    [InlineData("P2W", 1209600)]
    [InlineData("PT0.5S", 0.5)]
    [InlineData("PT1,25S", 1.25)]
    [InlineData("-PT10M", -600)]
    [InlineData("P0Y0M1D", 86400)]
    public void Durations_parse_iso_8601(string text, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ApiDuration.Parse(text));

    [Theory]
    [InlineData("P1Y")]
    [InlineData("P1M")]
    [InlineData("PT")]
    [InlineData("P")]
    [InlineData("1H")]
    [InlineData("PT1M1H")]
    [InlineData("P1H")]
    [InlineData("")]
    public void Invalid_durations_are_rejected(string text) =>
        Assert.False(ApiDuration.TryParse(text, out _));

    [Fact]
    public void Durations_round_trip()
    {
        foreach (var value in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(90), TimeSpan.FromDays(3) + TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(-0.25), TimeSpan.MaxValue, TimeSpan.MinValue + TimeSpan.FromTicks(1) })
        {
            Assert.Equal(value, ApiDuration.Parse(ApiDuration.Format(value)));
        }

        Assert.Equal("PT1H30M", ApiDuration.Format(TimeSpan.FromMinutes(90)));
        Assert.Equal("PT0S", ApiDuration.Format(TimeSpan.Zero));
        Assert.Equal("P1DT0.001S", ApiDuration.Format(TimeSpan.FromDays(1) + TimeSpan.FromMilliseconds(1)));
        Assert.Equal("\"PT2M\"", JsonSerializer.Serialize(TimeSpan.FromMinutes(2), ApiJson.CreateOptions()));
        Assert.Equal(TimeSpan.FromMinutes(2), JsonSerializer.Deserialize<TimeSpan?>("\"PT2M\"", ApiJson.CreateOptions()));
    }

    [Fact]
    public async Task Other_success_bodies_and_declared_headers_are_typed()
    {
        var handler = new RecordingHandler()
            .Respond(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("""{"percent":40,"stage":"running"}""", Encoding.UTF8, "application/json"),
                };
                response.Headers.TryAddWithoutValidation("Retry-After", "5");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v7\"");
                return Task.FromResult(response);
            })
            .Respond(HttpStatusCode.OK, """{"id":"d1","title":"T","summary":null}""");
        var client = new Features31Client(new HttpClient(handler));

        var pending = await client.GetDocumentResultAsync("d1");
        var done = await client.GetDocumentResultAsync("d1");

        Assert.True(pending.IsSuccess);
        Assert.Equal(202, pending.StatusCode);
        Assert.Null(pending.Value);
        Assert.True(pending.TryGetContent<Progress>(out var progress));
        Assert.Equal(40, progress.Percent);
        var headers = GetDocumentResponseHeaders.From(pending);
        Assert.Equal(5, headers.RetryAfter);
        Assert.Equal("\"v7\"", headers.ETag);
        Assert.Equal("d1", done.Value!.Id);
        Assert.Same(done.Value, done.Content);
        Assert.Null(GetDocumentResponseHeaders.From(done).RetryAfter);

        // The typed view of the success bodies: exactly the one of the response's status is set.
        var pendingBodies = GetDocumentResponseBodies.From(pending);
        var doneBodies = GetDocumentResponseBodies.From(done);
        Assert.Equal(40, pendingBodies.Accepted!.Percent);
        Assert.Null(pendingBodies.Ok);
        Assert.Equal("d1", doneBodies.Ok!.Id);
        Assert.Null(doneBodies.Accepted);
        Assert.Equal(202, pendingBodies.StatusCode);
    }

    [Fact]
    public void Header_values_parse_leniently()
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Date"] = ["Sun, 06 Nov 1994 08:49:37 GMT"],
            ["X-Ids"] = ["a, b", "c"],
            ["X-Count"] = ["many"],
            ["Location"] = ["/pets/42"],
        };

        Assert.Equal(new DateTimeOffset(1994, 11, 6, 8, 49, 37, TimeSpan.Zero), Runtime.Http.ApiHeaderValues.DateTimeOffset(headers, "date"));
        Assert.Equal(["a", "b", "c"], Runtime.Http.ApiHeaderValues.List(headers, "X-Ids"));
        Assert.Null(Runtime.Http.ApiHeaderValues.Int64(headers, "X-Count"));
        Assert.Null(Runtime.Http.ApiHeaderValues.String(headers, "Missing"));
        Assert.Equal("/pets/42", Runtime.Http.ApiHeaderValues.Uri(headers, "Location")!.OriginalString);
    }

    private sealed class ProgressLog : IProgress<ApiTransferProgress>
    {
        public List<ApiTransferProgress> Reports { get; } = [];

        public void Report(ApiTransferProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    [Fact]
    public async Task Uploads_and_downloads_report_progress()
    {
        var bytes = new byte[200_000];
        new Random(1).NextBytes(bytes);
        var handler = new RecordingHandler()
            .Respond(HttpStatusCode.OK, """{"url":"https://x/p.png","size":200000}""")
            .Respond(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentLength = bytes.Length } } }));
        var client = new PetstoreClient(new HttpClient(handler));
        var upload = new ProgressLog();
        var download = new ProgressLog();

        await client.UploadPetPhotoAsync(1, new FileContent(new MemoryStream(bytes), "p.png", "image/png") { Progress = upload });
        await using var photo = await client.DownloadPetPhotoAsync(1);
        var received = await photo.ReadAllBytesAsync(download);

        Assert.Equal(bytes, received);
        Assert.Equal(new ApiTransferProgress(0, bytes.Length), upload.Reports[0]);
        Assert.Equal(new ApiTransferProgress(bytes.Length, bytes.Length), upload.Reports[^1]);
        Assert.True(upload.Reports.Count > 2);
        Assert.Equal(1d, download.Reports[^1].Fraction);
        Assert.Equal(bytes.Length, download.Reports[^1].Transferred);
    }

    /// <summary>A body that delivers its bytes, then fails like a dropped connection.</summary>
    private sealed class BreakingStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            return read > 0 ? read : throw new IOException("connection reset");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new IOException("connection reset");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task Event_streams_reconnect_with_the_last_event_id()
    {
        const string first = """
            retry: 0
            id: 1
            data: {"percent":10,"stage":"running"}

            id: 2
            data: {"percent":20,"stage":"running"}


            """;
        const string second = """
            id: 3
            data: {"percent":100,"stage":"done"}


            """;
        var handler = new RecordingHandler()
            .Respond(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BreakingStream(Encoding.UTF8.GetBytes(first))) { Headers = { { "Content-Type", "text/event-stream" } } },
            }))
            .Respond(HttpStatusCode.OK, second, "text/event-stream");
        var client = new Features31Client(new HttpClient(handler));

        var events = new List<ServerSentEvent<Progress>>();
        await foreach (var e in ApiEventSource.Reconnecting<Progress>((lastEventId, ct) => client
            .WithOptions(o => { if (lastEventId is not null) o.DefaultHeaders["Last-Event-ID"] = lastEventId; })
            .StreamEventsResultAsync(ct)))
        {
            events.Add(e);
        }

        Assert.Equal(["1", "2", "3"], events.Select(e => e.Id));
        Assert.False(handler.Requests[0].Headers.ContainsKey("Last-Event-ID"));
        Assert.Equal("2", handler.Requests[1].Headers["Last-Event-ID"]);
        Assert.Equal(Stage.Done, events[^1].GetValue().Stage);
    }

    [Fact]
    public async Task Event_streams_stop_reconnecting_after_the_limit_or_on_http_errors()
    {
        var handler = new RecordingHandler()
            .Respond(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BreakingStream(Encoding.UTF8.GetBytes("retry: 0" + "\n\n"))) { Headers = { { "Content-Type", "text/event-stream" } } },
            }))
            .Respond(HttpStatusCode.Forbidden, "{}");
        var client = new Features31Client(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (var _ in ApiEventSource.Reconnecting<Progress>((_, ct) => client.StreamEventsResultAsync(ct), new ApiReconnectOptions { MaxReconnects = 3, DefaultRetry = TimeSpan.Zero }))
            {
            }
        });

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Intermediate_hierarchy_classes_read_and_write_their_subtypes()
    {
        var handler = new RecordingHandler()
            .Respond(HttpStatusCode.OK, """{"wheels":4,"seats":2,"topSpeed":310.5,"vehicleType":"SportsCar"}""")
            .Respond(HttpStatusCode.OK, """{"vehicleType":"Car","seats":5}""")
            .Respond(HttpStatusCode.OK, """{"vehicleType":"Hovercar","seats":1}""")
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NoContent);
        var client = new Features31Client(new HttpClient(handler));

        var sports = await client.GetCarAsync("a");
        var plain = await client.GetCarAsync("b");
        var unknown = await client.GetCarAsync("c");
        await client.PutCarAsync("a", new SportsCar { Seats = 2, TopSpeed = 300 });
        await client.PutCarAsync("b", new Car { Seats = 4, Wheels = 4 });

        var sportsCar = Assert.IsType<SportsCar>(sports);
        Assert.Equal(310.5, sportsCar.TopSpeed);
        Assert.Equal(4, sportsCar.Wheels);
        Assert.Equal(typeof(Car), plain.GetType());
        Assert.Equal(5, plain.Seats);
        Assert.Equal(typeof(Car), unknown.GetType());
        Assert.Equal("Hovercar", unknown.VehicleType);
        Assert.Equal("""{"topSpeed":300,"seats":2,"vehicleType":"SportsCar"}""", handler.Requests[3].Body);
        Assert.Equal("""{"vehicleType":"Car","wheels":4,"seats":4}""", handler.Requests[4].Body);

        // The hierarchy root still dispatches through the intermediate converter.
        var vehicle = System.Text.Json.JsonSerializer.Deserialize<Vehicle>("""{"vehicleType":"Car","seats":3}""", ApiJson.CreateOptions());
        Assert.Equal(3, Assert.IsType<Car>(vehicle).Seats);
    }
}
