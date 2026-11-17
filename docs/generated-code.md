# Generated code

Examples on this page come from the golden files in `tests/Golden/petstore`, generated from
`tests/Contracts/petstore.yaml`.

## Files

For a contract titled `Petstore` (CLI output directory shown; the source generator uses the same names as hint
names, prefixed with the contract file name, for example `petstore_yaml.Models.Pet.g.cs`):

```
Generated/
  .blazorapi-manifest                            CLI only: the files of the last run
  PetstoreClient.g.cs                            IPetstoreClient and PetstoreClient
  PetstoreClientServiceCollectionExtensions.g.cs AddPetstoreClient (generateDependencyInjection)
  PetstoreClientPagination.g.cs                  page walkers, when operations are paginated
  PetstoreClientMock.g.cs                        test double (generateMocks)
  Models/Pet.g.cs                                one file per model, enum or union
  Models/PetstoreFixtures.g.cs                   test data builders (generateMocks)
```

With `grouping=tag`, each client (`PetsClient`, `StoreClient`, and the default client for untagged operations) gets
its own client, registration, pagination and mock files. Clients, interfaces, models and the pagination and
fixture classes are `partial`, so you can add members in your own files. Output is deterministic: the
same contract and options always produce the same bytes.

## Clients

```csharp
public partial interface IPetstoreClient
{
    Task<Pet> GetPetAsync(long petId, CancellationToken cancellationToken = default);
    Task<ApiResult<Pet>> GetPetResultAsync(long petId, CancellationToken cancellationToken = default);
    Task<PetPage> ListPetsAsync(int? limit = null, string? cursor = null, IEnumerable<string>? tags = null,
        PetStatus? status = null, Guid? xRequestId = null, CancellationToken cancellationToken = default);
    Task<PhotoInfo> UploadPetPhotoAsync(long petId, FileContent @file, string? caption = null, CancellationToken cancellationToken = default);
    Task<FileResponse> DownloadPetPhotoAsync(long petId, CancellationToken cancellationToken = default);
    Task DeletePetAsync(long petId, CancellationToken cancellationToken = default);
    Task<ApiResult> DeletePetResultAsync(long petId, CancellationToken cancellationToken = default);
    // ...
}

public partial class PetstoreClient : IPetstoreClient
{
    public const string DefaultBaseAddress = "https://api.example.com/v1";
    public PetstoreClient(HttpClient httpClient);
    public PetstoreClient(HttpClient httpClient, ApiClientOptions? options);
    public ApiConnection Connection { get; }
    public static IReadOnlyList<ApiServer> Servers { get; }
    public PetstoreClient WithOptions(Action<ApiClientOptions> configure);
}
```

- Method names come from the `operationId` in PascalCase; without one, from the method and path
  (`GET /pets/{petId}` becomes `GetPetsPetId`).
- Parameters: path parameters, the body (or the fields of an inline multipart/form schema), then the other
  parameters; required ones first, optional ones default to `null`. `CancellationToken` is always last. Required
  reference arguments are checked for `null`.
- `DefaultBaseAddress` is the first server URL with its variables set to their defaults. The effective base address
  is `ApiClientOptions.BaseAddress`, else `HttpClient.BaseAddress`, else `DefaultBaseAddress`. The base path is kept
  (`https://host/v1` and `https://host/v1/` behave the same). An operation that declares its own server uses it
  (see [Servers](#servers)).
- Deprecated operations are marked `[Obsolete]`.
- Each request carries the operation id and its security requirements in `HttpRequestMessage.Options`
  (`ApiRequestOptionKeys.OperationId`, `ApiRequestOptionKeys.Security`), which authentication handlers and
  `MockApiHandler` use.

## Two methods per operation

| | `XxxAsync` | `XxxResultAsync` |
|---|---|---|
| Success | the body (`Task` when there is none) | `ApiResult<T>` (`ApiResult` when there is no body) |
| HTTP error, network error, timeout, unreadable body | throws an `ApiException` subclass | `IsSuccess == false`, details in `Error` |
| Caller cancellation | `OperationCanceledException` | `OperationCanceledException` |

`ApiResult` has `IsSuccess`, `StatusCode` (0 when no response arrived), `Headers`, `Error`, `GetHeader(name)` and
`EnsureSuccess()`; `ApiResult<T>` adds `Value` and `GetValueOrThrow()`. `ApiResults.Success`, `ApiResults.NoContent`
and `ApiResults.Failure` create results directly (for mocks and hand-written code).

## Responses

Each operation has a static `ApiResponseHandler<T>` that says which statuses are success and how each body is read:

```csharp
private static readonly ApiResponseHandler<Pet> GetPetResponses = new ApiResponseHandler<Pet>()
    .Success("200", ApiBody.ReadJson<Pet>)
    .Error("404", ApiBody.JsonError<Error>);
```

- Statuses match exactly first, then ranges (`2XX`), then `default`. An undeclared 2xx status is read like the
  declared success body; an undeclared non-2xx status is an error.
- JSON bodies are read with System.Text.Json from the response stream; `text/*` as `string`; binary bodies as a
  `FileResponse` that streams the content (dispose it to release the connection; `ReadAllBytesAsync` buffers it).
- Success responses without a body (204, 205, 304, `HEAD`, or `Content-Length: 0`) succeed with a default `Value`.
- The first success body type is the result value. Other success statuses with a different JSON or text body
  (a 202 job status next to a 200 resource) are read into `ApiResult.Content` with a default `Value` (BAPI6001, info):

  ```csharp
  var result = await api.GetDocumentResultAsync(id);
  if (result.TryGetContent<Progress>(out var progress)) { /* 202: still processing */ }
  else if (result.IsSuccess) { Document document = result.Value!; }
  ```

  `Content` is the value itself for the main success body. Binary and streamed alternative bodies are not read.
  `ApiResults.SuccessWithContent<T>(content, 202)` creates such results in tests.

## Response headers

Operations that declare response headers get a `{Operation}ResponseHeaders` class (in `{Client}ResponseHeaders.g.cs`)
with one typed, nullable property per header (`ETag`, `Location` as `Uri`, `Retry-After` as `long`, `X-Rate-Limit-*`,
dates from HTTP-dates, enums, comma-separated lists) plus `Headers` with every header:

```csharp
var result = await api.CreatePetResultAsync(newPet);
var headers = CreatePetResponseHeaders.From(result);     // also From(ApiError) and new(dictionary)
Uri? location = headers.Location;
```

Absent or malformed headers are `null`; reading headers never fails a call. `ApiHeaderValues` has the parsers.
`generateResponseHeaders=false` turns the classes off.

## Errors

`ApiError` describes every failure:

| Member | Meaning |
|---|---|
| `Kind` | `Http`, `Network`, `Timeout` or `Deserialization` |
| `Message` | description including the operation id (never credentials) |
| `StatusCode`, `ReasonPhrase`, `Headers` | when a response arrived |
| `Body` | error body text, truncated to `ApiClientOptions.MaxErrorBodyLength` (64 KiB) |
| `Content` | error body read as the type the contract declares for that status |
| `Problem` | `Content` as `ApiProblemDetails`, when it is one |
| `Exception`, `OperationId` | underlying exception; the failed operation |

`TryGetContent<T>(out T content)` reads `Content` as a type. Error bodies are typed as follows: a declared JSON schema
gives the generated type (`Error`, or the contract's own `ProblemDetails` model); `application/problem+json` without a
schema gives `ApiProblemDetails` (RFC 9457, with `Errors` and `Extensions`); text gives a `string`; anything
undeclared, truncated or unreadable leaves `Content` null and `Body` set.

The value methods throw `ApiException` (`Error`, `StatusCode`, `Content`, `Problem`) for HTTP errors and
`ApiNetworkException`, `ApiTimeoutException` or `ApiDeserializationException` for the other kinds
(`ApiError.ToException()` makes the same choice).

```csharp
try
{
    await api.CreatePetAsync(new NewPet { Name = "", Kind = "dog" });
}
catch (ApiException ex) when (ex.Content is ProblemDetails problem)
{
    foreach (var (field, messages) in problem.Errors ?? new Dictionary<string, IReadOnlyList<string>>())
        Console.WriteLine($"{field}: {string.Join(" ", messages)}");
}
catch (ApiTimeoutException) { /* ApiClientOptions.Timeout or HttpClient.Timeout elapsed */ }
```

## Upload and download progress

`FileContent.Progress` receives `ApiTransferProgress` (bytes transferred, total when known, `Fraction`) while a file is
sent; `FileResponse.CopyToAsync(destination, progress)` and `ReadAllBytesAsync(progress)` report it while a download is
read:

```csharp
var progress = new Progress<ApiTransferProgress>(p => _percent = p.Fraction ?? 0);
await api.UploadPetPhotoAsync(id, new FileContent(stream, "photo.png", "image/png") { Progress = progress });

await using var photo = await api.DownloadPetPhotoAsync(id);
await photo.CopyToAsync(file, progress);
```

Seekable streams report a total and are rewound when a request is sent again (retries, authentication). In Blazor
WebAssembly the browser sends request bodies, so upload progress reflects handing the body to the browser.

## Per-call options

`WithOptions` (also on the interface) returns a client that shares the `HttpClient` but uses a modified copy of the
options; the original client is unchanged. Use it for per-call headers (conditional requests, idempotency keys,
correlation ids), timeouts or base addresses:

```csharp
Pet pet = await api.WithOptions(o => o.DefaultHeaders["If-Match"] = etag).UpdatePetAsync(42, changed);
var slow = api.WithOptions(o => o.Timeout = TimeSpan.FromMinutes(5));
```

`ApiClientOptions.Clone()` makes the copy (headers and operation base addresses are copied, JSON options and callbacks
shared); classes derived from `ApiClientOptions` override it. Generated mocks return themselves.

## Servers

`{Client}.Servers` lists the document's servers as `ApiServer` values. `Resolve` fills their variables (checking
`enum` restrictions; unset variables use their defaults):

```csharp
builder.Services.AddPetstoreClient(o => o.BaseAddress = PetstoreClient.Servers[0].Resolve(new Dictionary<string, string> { ["region"] = "eu" }));
```

Operations (or paths) that declare their own `servers` send requests to that server regardless of `BaseAddress`
(for example an upload host). `ApiClientOptions.OperationBaseAddresses["uploadFile"] = new Uri(...)` overrides it per
operation, which tests and staging environments need.

## Streamed responses

Operations whose success response is `text/event-stream` or a JSON sequence (`application/jsonl`,
`application/x-ndjson`, `application/json-seq`) return an `ApiStream<T>`
([OpenAPI support](openapi-support.md#streamed-responses)). The result arrives when the response headers do; items are
read as they arrive:

```csharp
await using ApiStream<ServerSentEvent<Completion>> events = await api.StreamCompletionAsync(request);
await foreach (ServerSentEvent<Completion> e in events)
{
    if (e.Data == "[DONE]") break;
    Console.Write(e.GetValue().Text);
}
```

- A stream can be enumerated once; it owns the HTTP response, which is released when the enumeration ends or the
  stream is disposed. Dispose streams you do not read to the end.
- HTTP errors, network failures and timeouts before the first byte are reported like any other call. Failures while
  items are read are thrown by the enumeration: `ApiNetworkException` (the connection broke) and
  `ApiDeserializationException` (an item does not match its type). `ApiClientOptions.Timeout` covers only the time to
  the response headers; cancel the enumeration with a token (`WithCancellation`) to bound it.
- `ApiStream.From(items)` creates streams for mocks and tests; `ServerSentEvent<T>.FromValue(value)` creates events.

### Reconnecting event streams

`ApiEventSource.Reconnecting` reopens a server-sent event stream when the connection breaks, after the server's
`retry:` delay (3 s by default), sending the id of the last event received so the server can resume:

```csharp
var events = ApiEventSource.Reconnecting<Notification>((lastEventId, ct) => api
    .WithOptions(o => { if (lastEventId is not null) o.DefaultHeaders["Last-Event-ID"] = lastEventId; })
    .StreamNotificationsResultAsync(ct));
await foreach (var e in events) { /* ... */ }
```

`ApiReconnectOptions` sets `MaxReconnects` (5 consecutive failures), `DefaultRetry`, and `ReconnectWhenEnded` (false: a
stream that ends normally, such as a generated answer, is not replayed). HTTP error responses end the sequence with
their `ApiException`; a 204 ends it quietly. The sequence works with `ApiStreamView`'s `Items`.

## ApiClientOptions

| Property | Default | Purpose |
|---|---|---|
| `BaseAddress` | `null` | Overrides `HttpClient.BaseAddress` and the contract's server. |
| `JsonSerializerOptions` | `ApiJson.CreateOptions()` | Web defaults, strict numbers, no naming policy (names come from `[JsonPropertyName]`). |
| `Timeout` | `null` | Per-request timeout including the body; `null` relies on `HttpClient.Timeout`. |
| `DefaultHeaders` | empty | Headers added to every request. |
| `OnRequest`, `OnResponse` | `null` | Callbacks before sending and after the response headers arrive. |
| `MaxErrorBodyLength` | 65,536 | Characters of an error body kept in `ApiError.Body`. |
| `OperationBaseAddresses` | empty | Base addresses by operation id; they override the servers operations declare. |

## Dependency injection

```csharp
IHttpClientBuilder builder = services.AddPetstoreClient(options => options.Timeout = TimeSpan.FromSeconds(20));
```

The generated method calls `AddApiClient<IPetstoreClient>` (namespace `Microsoft.Extensions.DependencyInjection`): it
registers a named `HttpClient` (`"PetstoreClient"`), named `ApiClientOptions` with the same name, and the client as
a transient service. Use the returned builder for [authentication](authentication.md), resilience or logging
handlers, and `ConfigurePrimaryHttpMessageHandler` in tests. Without interfaces (`generateInterfaces=false`) the
class itself is registered.

## Models

```csharp
[JsonConverter(typeof(PetJsonConverter))]
public abstract partial class Pet
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Id { get; set; }                       // readOnly: nullable, omitted from requests

    [JsonPropertyName("name")]
    [Required(AllowEmptyStrings = true)]
    [StringLength(100, MinimumLength = 1)]
    public required string Name { get; set; }
    // ...
}

public partial class Dog : Pet
{
    public Dog() { Kind = "dog"; }                     // discriminator set by the subtype
    [JsonPropertyName("barkVolume")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BarkVolume { get; set; }
}
```

Properties use the JSON name explicitly, follow the [required and nullable rules](openapi-support.md#required-and-nullable)
and are marked `[Obsolete]` when deprecated. Objects that allow additional properties get an
`AdditionalProperties` dictionary (`[JsonExtensionData]`).

**DataAnnotations** (`generateDataAnnotations`, on by default) make models validate in Blazor `EditForm`s:
`[Required(AllowEmptyStrings = true)]` for required non-nullable references, `[StringLength]`/`[MinLength]` from
`maxLength`/`minLength`, `[RegularExpression]` from valid `pattern`s, `[MinLength]`/`[MaxLength]` from
`minItems`/`maxItems`, and `[Range]` from `minimum`/`maximum` (with `MinimumIsExclusive`/`MaximumIsExclusive` for
exclusive bounds).

**XML documentation** (`generateDocumentation`) comes from `summary`, `description` and `title`, plus remarks for
read-only, write-only, defaults and merge-patch semantics. Operation remarks show the method, path and operation id.
Contract text is escaped, so it cannot break out of the comment.

## Trimming and native AOT

Generated code uses System.Text.Json with the reflection-based resolver by default. With `generateJsonContext=true`
(CLI) a generated `{Api}JsonContext` covers every serialized type and is used by the generated constructors and
registration methods, so trimmed, native AOT and WebAssembly apps need no reflection. Otherwise add a
`JsonSerializerContext` through `ApiClientOptions.JsonSerializerOptions`; see
[Troubleshooting](troubleshooting.md#trimming-and-native-aot).
