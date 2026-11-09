using System.Collections.Concurrent;
using BlazorApi.Runtime.Json;

namespace Petstore.BlazorApp.Api;

/// <summary>
/// An in-memory implementation of the Petstore contract, so the sample runs without external services. It uses the
/// generated models for its own JSON, which keeps server and client on the same contract.
/// </summary>
public sealed class PetStore
{
    public const string DemoToken = "demo-token";
    public const string DemoApiKey = "demo-key";

    private readonly ConcurrentDictionary<long, Pet> _pets = new();
    private long _nextId;

    public PetStore()
    {
        Add(new Dog { Name = "Rex", Status = PetStatus.Available, BarkVolume = 7 });
        Add(new Cat { Name = "Tom", Indoor = true, Status = PetStatus.Pending });
        Add(new Dog { Name = "Fido", Status = PetStatus.Sold, Tag = "senior" });
        for (var i = 1; i <= 9; i++)
        {
            Add(i % 2 == 0 ? new Cat { Name = $"Cat {i}", Indoor = i % 4 == 0 } : new Dog { Name = $"Dog {i}" });
        }
    }

    public IEnumerable<Pet> All => _pets.Values.OrderBy(p => p.Id);

    public Pet Add(Pet pet)
    {
        pet.Id = Interlocked.Increment(ref _nextId);
        pet.CreatedAt = DateTimeOffset.UtcNow;
        _pets[pet.Id.Value] = pet;
        return pet;
    }

    public Pet? Find(long id) => _pets.TryGetValue(id, out var pet) ? pet : null;

    public bool Remove(long id) => _pets.TryRemove(id, out _);
}

/// <summary>The API endpoints, under <c>/v1</c> as the contract's server URL says.</summary>
public static class PetstoreEndpoints
{
    public static void MapPetstoreApi(this WebApplication app)
    {
        var json = ApiJson.CreateOptions();
        var api = app.MapGroup("/v1");
        api.MapGet("/health", () => Results.Text("healthy"));

        var pets = api.MapGroup("/pets").AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.Headers.Authorization == "Bearer " + PetStore.DemoToken
                ? await next(context)
                : Results.Json(new Error { Code = 401, Message = "Sign in first." }, json, statusCode: 401));

        pets.MapGet("/", (PetStore store, int? limit, string? cursor, string? status) =>
        {
            var size = Math.Clamp(limit ?? 20, 1, 100);
            var after = long.TryParse(cursor, out var parsed) ? parsed : 0;
            var filtered = store.All.Where(p => p.Id > after &&
                (status is null || (PetStatusJsonConverter.TryParse(status, out var s) && p.Status == s))).ToList();
            var page = filtered.Take(size).ToList();
            var response = new PetPage
            {
                Items = page,
                NextCursor = filtered.Count > size ? page[^1].Id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            };
            return Results.Json(response, json);
        });

        pets.MapPost("/", async (PetStore store, HttpRequest request) =>
        {
            var body = await request.ReadFromJsonAsync<NewPet>(json);
            var errors = new Dictionary<string, string[]>();
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
            {
                errors["name"] = ["Every pet needs a name."];
            }

            if (body is not null && body.Kind is not ("dog" or "cat"))
            {
                errors["kind"] = ["Only dogs and cats are supported."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, title: "The pet is invalid.", statusCode: 422);
            }

            Pet pet = body!.Kind == "dog" ? new Dog { Name = body.Name } : new Cat { Name = body.Name, Indoor = true };
            pet.Tag = body.Tag;
            pet.Status = body.Status ?? PetStatus.Available;
            return Results.Json(store.Add(pet), json, statusCode: 201);
        });

        pets.MapGet("/{petId:long}", (PetStore store, long petId) =>
            store.Find(petId) is { } pet ? Results.Json(pet, json) : NotFound(petId, json));

        pets.MapPatch("/{petId:long}", async (PetStore store, long petId, HttpRequest request) =>
        {
            if (store.Find(petId) is not { } pet)
            {
                return NotFound(petId, json);
            }

            var patch = await request.ReadFromJsonAsync<PetPatch>(json);
            if (patch?.Name is { Length: > 0 } name)
            {
                pet.Name = name;
            }

            if (patch?.Tag is { HasValue: true } tag)
            {
                pet.Tag = tag.Value;
            }

            return Results.Json(pet, json);
        });

        pets.MapDelete("/{petId:long}", (PetStore store, long petId) =>
            store.Remove(petId) ? Results.NoContent() : NotFound(petId, json));

        api.MapGet("/store/inventory", (PetStore store, HttpRequest request) =>
            request.Headers["X-API-Key"] == PetStore.DemoApiKey
                ? Results.Json(store.All.GroupBy(p => p.Status ?? PetStatus.Available).ToDictionary(g => PetStatusJsonConverter.ToWireValue(g.Key), g => g.Count()), json)
                : Results.Json(new Error { Code = 401, Message = "An API key is required." }, json, statusCode: 401));
    }

    private static IResult NotFound(long id, System.Text.Json.JsonSerializerOptions json) =>
        Results.Json(new Error { Code = 404, Message = $"There is no pet {id}." }, json, statusCode: 404);
}
