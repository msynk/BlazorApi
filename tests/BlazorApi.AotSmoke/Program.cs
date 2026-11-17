using System.Text.Json;
using AotSmoke.Petstore;
using BlazorApi.Runtime;
using BlazorApi.Runtime.Json;

// Calls a mock of tests/Contracts/petstore.yaml through a client generated with generateJsonContext=true. Every
// serialized type must come from the generated JsonSerializerContext: reflection-based JSON is disabled.
if (JsonSerializer.IsReflectionEnabledByDefault)
{
    return Fail("reflection-based serialization is enabled; the smoke test would prove nothing");
}

var client = new PetstoreClient(new HttpClient(), new ApiClientOptions
{
    BaseAddress = new Uri(args.Length > 0 ? args[0] : "http://localhost:4010/v1/"),
    JsonSerializerOptions = ApiJson.CreateOptions(PetstoreJsonContext.Default),
});

var pet = await client.GetPetAsync(42);
if (pet is not (Dog or Cat))
{
    return Fail($"getPet returned {pet.GetType().Name}, not a concrete subtype");
}

var page = await client.ListPetsAsync(limit: 2, tags: ["a"], status: PetStatus.Available);
var created = await client.CreatePetAsync(new NewPet { Name = "Rex", Kind = "dog" });
var patched = await client.PatchPetAsync(42, new PetPatch { Tag = null });
var inventory = await client.GetInventoryAsync();
var missing = await client.WithOptions(o => o.DefaultHeaders["Prefer"] = "code=404").GetPetResultAsync(1);
if (missing.Error is not { Kind: ApiErrorKind.Http, StatusCode: 404 } || missing.Error.Content is not Error)
{
    return Fail($"the 404 was not read as the typed Error body ({missing.Error?.Message})");
}

Console.WriteLine($"getPet {pet.GetType().Name}, listPets {page.Items.Count}, createPet {created.Name}, patchPet {patched.Name}, inventory {inventory.Count}, 404 typed: ok");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine("AOT smoke test failed: " + message);
    return 1;
}
