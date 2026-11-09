# Testing

BlazorApi supports three levels of testing without a running server:

1. **Generated mocks** replace the client interface in unit tests of your own code.
2. **`MockApiHandler`** runs the real generated client (serialization, parameters, errors, handlers) against
   configured HTTP responses.
3. **Contract compatibility checks** fail a test or a build when a contract change breaks clients.

The runtime pieces are in `Msynk.BlazorApi.Testing` (namespace `BlazorApi.Testing`). It is framework-agnostic:
verification failures throw `ApiMockVerificationException`, which any test framework reports.

## Generated mocks

Enable `GenerateMocks=true`. For each client the generator emits `{Client}Mock` (derived from `ApiMock` and
implementing the client interface) and, once per contract, `{Api}Fixtures` in the models namespace.

```xml
<OpenApiContract Include="Contracts/petstore.yaml" Options="GenerateMocks=true" />
<PackageReference Include="Msynk.BlazorApi.Testing" Version="0.1.0-preview" />
```

Mocks are generated next to the client, in the same project, so that project must reference
`Msynk.BlazorApi.Testing`. If the client lives in production code, either accept that reference, generate the
client in a separate library that tests reference, or test at the HTTP level with `MockApiHandler`, which needs no
generated code. `samples/Petstore.Tests` generates its own copy of the client with mocks.

```csharp
var mock = new PetstoreClientMock()
    .SetupGetPet(id => PetstoreFixtures.CreateDog(dog => { dog.Id = id; dog.Status = PetStatus.Available; }))
    .SetupPatchPet(PetstoreFixtures.CreateDog());

var message = await new AdoptionService(mock).AdoptAsync(7);

mock.Verify(PetstoreClientMock.Operations.PatchPet, call =>
    call.Argument<long>("petId") == 7 && call.Argument<PetPatch>("body").Tag.Value == "adopted");
```

Generated members (chainable):

| Member | Effect |
|---|---|
| `Setup{Op}(value)` | succeed with `value` and the status of the operation's first success response with a body |
| `Setup{Op}(Func<args..., value>)` | compute the value from the call arguments |
| `Setup{Op}()` | succeed, for operations without a response body |
| `Operations.{Op}` | the operation id constants used by the members below |

Inherited from `ApiMock` (take the operation id):

| Member | Effect |
|---|---|
| `Setup<T>(id, call => ApiResult<T>)`, `Setup(id, call => ApiResult)`, `SetupAsync<T>(id, ...)` | answer with any result |
| `SetupError(id, statusCode, content)` | fail with an HTTP error; `content` becomes `ApiError.Content` |
| `SetupNetworkFailure(id, exception)` | fail with a network error |
| `SetupTimeout(id)` | fail with a timeout (`ApiTimeoutException` from value methods) |
| `SetupException(id, exception)` | throw an exception a real client would not convert |
| `SetupDelay(id, delay)` | delay the answer (cancellation is honored) |
| `Verify(id, times = 1)`, `Verify(id, predicate)`, `VerifyNoOtherCalls(ids...)` | verify calls |
| `Calls`, `CallsTo(id)`, `Reset()` | recorded `ApiMockCall`s (`OperationId`, `Arguments`, `Argument<T>(name)`), clear |

Arguments are recorded by parameter name; the body is named `body`. Operations without a setup fail with HTTP 501
so a missing setup is visible. Both `XxxAsync` and `XxxResultAsync` go through the setup, and the value methods throw
the same exceptions as the real client.

```csharp
var mock = new PetstoreClientMock();
mock.SetupError(PetstoreClientMock.Operations.GetPet, 404, new Error { Code = 404, Message = "missing" });
mock.SetupTimeout(PetstoreClientMock.Operations.GetHealth);
```

## Fixtures

`{Api}Fixtures.Create{Model}(configure)` builds representative instances: required properties are set from
`example`/`examples`, `default` or a type-appropriate value, nested models are built a few levels deep, and abstract
hierarchy roots return their first concrete subtype (`CreatePet()` returns a `Dog`). Values are deterministic, so
fixtures are safe for snapshot tests.

```csharp
Dog dog = PetstoreFixtures.CreateDog(d => d.Name = "Rex");
PetPage page = PetstoreFixtures.CreatePetPage();
```

## MockApiHandler

`MockApiHandler` is an `HttpMessageHandler` that answers requests by operation id (read from the request options the
generated client sets). Every request is recorded with its body.

```csharp
var handler = new MockApiHandler()
    .RespondJson("getPet", PetstoreFixtures.CreateCat(c => c.Name = "Tom"))
    .RespondOnce("listPets", HttpStatusCode.ServiceUnavailable)       // first call only
    .RespondJson("listPets", new PetPage { Items = [PetstoreFixtures.CreateCat()] })
    .Throw("getHealth", new HttpRequestException("offline"))
    .RespondJson("patchPet", PetstoreFixtures.CreateCat());
var client = new PetstoreClient(new HttpClient(handler));

await client.PatchPetAsync(3, new PetPatch { Tag = "adopted" });

RecordedApiRequest patch = Assert.Single(handler.RequestsTo("patchPet"));
Assert.Equal("""{"tag":"adopted"}""", patch.Body);
handler.Verify("getPet", times: 0);
```

| Member | Effect |
|---|---|
| `RespondJson<T>(id, value, status = OK)` | answer every call with a JSON value (serialized with the client's default JSON options, or the options passed to the constructor) |
| `Respond(id, status, body, contentType = "application/json")` | answer with a status and raw body |
| `Respond(id, (request, ct) => Task<HttpResponseMessage>)` | answer with a function |
| `RespondOnceJson<T>(...)`, `RespondOnce(...)` | queue an answer for the next call (used before the default answer) |
| `Throw(id, exception)` | throw, for example `HttpRequestException` to simulate a network failure |
| `Delay(id, delay)` | delay the current default answer (call it after `Respond...`) |
| `Requests`, `RequestsTo(id)`, `Verify(id, times = 1)` | recorded `RecordedApiRequest`s: `OperationId`, `Method`, `Uri`, `Headers`, `Body`, `BodyJson` |

Unconfigured operations answer `501 Not Implemented`. Without a base address the client uses the contract's server
URL, which the handler never contacts. With dependency injection, install it as the primary handler so registered
authentication handlers run too:

```csharp
services.AddPetstoreClient()
    .AddBearerToken(_ => new StaticAccessTokenProvider("test"), "bearerAuth")
    .ConfigurePrimaryHttpMessageHandler(() => handler);
```

## Contract compatibility tests

Compare the current contract with the last published one in a test (or use `api-gen diff` in CI, see
[CLI](cli.md#diff)). This uses the engine package `Msynk.BlazorApi.OpenApi`:

```csharp
static ApiModel Load(string path)
{
    var result = OpenApiLoader.LoadFile(path);
    Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
    return ApiModelBuilder.Build(result.Document!, new DiagnosticBag());
}

var report = ContractComparer.Compare(Load("Contracts/baseline/petstore.yaml"), Load("Contracts/petstore.yaml"));
Assert.False(report.HasChangesAtLeast(ChangeSeverity.Breaking), report.ToText());
```

See [Compatibility](compatibility.md) for the rules and change kinds. `samples/Petstore.Tests` contains all of the
above.
