using Microsoft.AspNetCore.Http.Features;
using System.Globalization;
using System.Text.Json;
using BlazorApi.Runtime.Json;

namespace BlazorApi.Demo.BlazorApp.Api;

/// <summary>
/// The demo API: every operation of <c>demo/Contracts/petstore.yaml</c>, under <c>/v1</c> as the contract's server
/// URL says. It is deliberately small; the interesting part is the generated client that calls it from the pages.
/// </summary>
public static class PetstoreEndpoints
{
    /// <summary>The largest photo the API accepts.</summary>
    public const long MaxPhotoSize = 1024 * 1024;

    /// <summary>
    /// A request header (not part of the contract) that makes the API wait the given number of milliseconds before
    /// answering. The playground uses it to demonstrate client timeouts and cancellation.
    /// </summary>
    public const string DelayHeader = "X-Demo-Delay";

    private static readonly JsonSerializerOptions Json = ApiJson.CreateOptions();

    public static void MapPetstoreApi(this WebApplication app)
    {
        var api = app.MapGroup("/v1").AddEndpointFilter(async (context, next) =>
        {
            if (int.TryParse(context.HttpContext.Request.Headers[DelayHeader], out var delay) && delay > 0)
            {
                await Task.Delay(Math.Min(delay, 10_000), context.HttpContext.RequestAborted);
            }

            return await next(context);
        });

        // security: [] in the contract: no credentials needed.
        api.MapGet("/health", () => Results.Text("healthy"));

        // The contract's default security requirement (bearerAuth) applies to everything else but the inventory.
        var secured = api.MapGroup("/").AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.Headers.Authorization == "Bearer " + PetStore.DemoToken
                ? await next(context)
                : Failure(401, "Sign in first: this operation requires a bearer token."));

        MapPets(secured.MapGroup("/pets"));

        secured.MapPost("/store/orders", PlaceOrderAsync);

        api.MapGet("/store/inventory", (PetStore store, HttpRequest request) =>
            request.Headers["X-API-Key"] == PetStore.DemoApiKey
                ? Results.Json(Enum.GetValues<PetStatus>().ToDictionary(PetStatusJsonConverter.ToWireValue, s => store.All.Count(p => (p.Status ?? PetStatus.Available) == s)), Json)
                : Failure(401, "An API key is required."));
    }

    private static PetActivityKind ToKind(string kind) =>
        PetActivityKindJsonConverter.TryParse(kind, out var parsed) ? parsed : throw new ArgumentOutOfRangeException(nameof(kind));

    private static void MapPets(RouteGroupBuilder pets)
    {
        pets.MapGet("/", (PetStore store, HttpResponse response, int? limit, string? cursor, string? status, string[]? tags) =>
        {
            PetStatus? wanted = null;
            if (status is not null)
            {
                if (!PetStatusJsonConverter.TryParse(status, out var parsed))
                {
                    return Failure(400, $"'{status}' is not a pet status.");
                }

                wanted = parsed;
            }

            var size = Math.Clamp(limit ?? 20, 1, 100);
            var after = long.TryParse(cursor, out var id) ? id : 0;
            var matching = store.All.Where(p => (wanted is null || p.Status == wanted) &&
                (tags is not { Length: > 0 } || (p.Tag is not null && tags.Contains(p.Tag, StringComparer.OrdinalIgnoreCase)))).ToList();
            var remaining = matching.Where(p => p.Id > after).ToList();
            var page = remaining.Take(size).ToList();

            response.Headers["X-Total-Count"] = matching.Count.ToString(CultureInfo.InvariantCulture);
            return Results.Json(new PetPage
            {
                Items = page,
                NextCursor = remaining.Count > size ? page[^1].Id!.Value.ToString(CultureInfo.InvariantCulture) : null,
            }, Json);
        });

        pets.MapPost("/", async (PetStore store, HttpRequest request) =>
        {
            var (body, invalid) = await ReadJsonAsync<NewPet>(request);
            if (invalid is not null)
            {
                return invalid;
            }

            var errors = new Dictionary<string, string[]>();
            ValidateName(body!.Name, errors);
            if (body.Kind is not ("dog" or "cat"))
            {
                errors["kind"] = [$"'{body.Kind}' is not supported: only dogs and cats live here."];
            }

            if (body.Tag is { Length: > 30 })
            {
                errors["tag"] = ["Keep the tag to 30 characters or fewer."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, title: "The pet is invalid.", statusCode: 422);
            }

            Pet pet = body.Kind == "dog" ? new Dog { Name = body.Name.Trim() } : new Cat { Name = body.Name.Trim(), Indoor = true };
            pet.Tag = string.IsNullOrWhiteSpace(body.Tag) ? null : body.Tag.Trim();
            pet.Status = body.Status ?? PetStatus.Available;
            return Results.Json(store.Add(pet), Json, statusCode: 201);
        });

        // The ETag is a hash of the pet's JSON: a client that sends it back in If-None-Match gets 304 while the pet is
        // unchanged (the generated client's AddApiCaching() does this and answers from its cache).
        pets.MapGet("/{petId:long}", (PetStore store, long petId, HttpRequest request, HttpResponse response) =>
        {
            if (store.Find(petId) is not { } pet)
            {
                return NotFound(petId);
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(pet, Json);
            var etag = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(json), 0, 8) + "\"";
            response.Headers.ETag = etag;
            return request.Headers.IfNoneMatch == etag
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(json, "application/json");
        });

        // Server-sent events: one activity every 700 ms, eight in all, then the stream ends.
        pets.MapGet("/{petId:long}/activity", async (PetStore store, long petId, HttpResponse response, CancellationToken cancellationToken) =>
        {
            if (store.Find(petId) is not { } pet)
            {
                return NotFound(petId);
            }

            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-store";
            (string Kind, string Message)[] activities =
            [
                ("visit", $"A family came to meet {pet.Name}."),
                ("play", $"{pet.Name} chased a ball across the yard."),
                ("meal", $"{pet.Name} finished breakfast."),
                ("nap", $"{pet.Name} is napping in the sun."),
                ("visit", "A volunteer stopped by for a walk."),
                ("play", $"{pet.Name} found a new favorite toy."),
                ("meal", "Treat time."),
                ("nap", $"{pet.Name} is asleep. See you tomorrow!"),
            ];
            for (var i = 0; i < activities.Length; i++)
            {
                var activity = new PetActivity { At = DateTimeOffset.UtcNow, Kind = ToKind(activities[i].Kind), Message = activities[i].Message };
                await response.WriteAsync($"id: {i + 1}\ndata: {JsonSerializer.Serialize(activity, Json)}\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
                await Task.Delay(700, cancellationToken);
            }

            return Results.Empty;
        });

        // PUT replaces the whole pet; the polymorphic body arrives as a Dog or a Cat.
        pets.MapPut("/{petId:long}", async (PetStore store, long petId, HttpRequest request) =>
        {
            if (store.Find(petId) is null)
            {
                return NotFound(petId);
            }

            var (pet, invalid) = await ReadJsonAsync<Pet>(request);
            if (invalid is not null)
            {
                return invalid;
            }

            if (pet is UnrecognizedPet)
            {
                return Failure(400, $"'{pet.Kind}' is not a kind of pet this store knows.");
            }

            var errors = new Dictionary<string, string[]>();
            ValidateName(pet!.Name, errors);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, title: "The pet is invalid.", statusCode: 422);
            }

            store.Replace(petId, pet);
            return Results.Json(pet, Json);
        });

        // A JSON merge patch: absent properties stay as they are, "tag": null clears the tag.
        pets.MapPatch("/{petId:long}", async (PetStore store, long petId, HttpRequest request) =>
        {
            if (store.Find(petId) is not { } pet)
            {
                return NotFound(petId);
            }

            var (patch, invalid) = await ReadJsonAsync<PetPatch>(request);
            if (invalid is not null)
            {
                return invalid;
            }

            if (patch!.Name is not null)
            {
                var errors = new Dictionary<string, string[]>();
                ValidateName(patch.Name, errors);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors, title: "The change is invalid.", statusCode: 422);
                }

                pet.Name = patch.Name.Trim();
            }

            if (patch.Tag.HasValue)
            {
                pet.Tag = string.IsNullOrWhiteSpace(patch.Tag.Value) ? null : patch.Tag.Value.Trim();
            }

            return Results.Json(pet, Json);
        });

        pets.MapDelete("/{petId:long}", (PetStore store, long petId) =>
            store.Remove(petId) ? Results.NoContent() : NotFound(petId));

        pets.MapGet("/{petId:long}/photo", (PetStore store, long petId) =>
            store.Find(petId) is null ? NotFound(petId)
            : store.FindPhoto(petId) is { } photo ? Results.File(photo.Content, photo.ContentType, $"pet-{petId}.png")
            : Failure(404, "This pet has no photo yet."));

        // multipart/form-data with a "file" part and an optional "caption".
        pets.MapPost("/{petId:long}/photo", async (PetStore store, long petId, HttpRequest request) =>
        {
            if (store.Find(petId) is null)
            {
                return NotFound(petId);
            }

            if (!request.HasFormContentType)
            {
                return Failure(415, "Send the photo as multipart/form-data.");
            }

            // Refuse oversized uploads before reading them (the form, with some room for the other fields).
            if (request.ContentLength > MaxPhotoSize + (64 * 1024))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The photo must be 1 MB or smaller."] }, statusCode: 422);
            }

            var form = await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = MaxPhotoSize + (64 * 1024) }, request.HttpContext.RequestAborted);
            if (form.Files.GetFile("file") is not { Length: > 0 } file)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Choose a photo to upload."] }, statusCode: 422);
            }

            if (file.Length > MaxPhotoSize)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The photo must be 1 MB or smaller."] }, statusCode: 422);
            }

            using var content = new MemoryStream();
            await file.CopyToAsync(content);
            store.SetPhoto(petId, new StoredPhoto(content.ToArray(), file.ContentType ?? "application/octet-stream", form["caption"]));
            var url = new Uri($"{request.Scheme}://{request.Host}{request.PathBase}/v1/pets/{petId}/photo");
            return Results.Json(new PhotoInfo { Url = url, Size = file.Length }, Json);
        });
    }

    // application/x-www-form-urlencoded: adopting an available pet marks it sold.
    private static async Task<IResult> PlaceOrderAsync(PetStore store, HttpRequest request)
    {
        if (!request.HasFormContentType)
        {
            return Failure(415, "Send the order as a form.");
        }

        var form = await request.ReadFormAsync();
        if (!long.TryParse(form["petId"], CultureInfo.InvariantCulture, out var petId) || store.Find(petId) is not { } pet)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["petId"] = ["Choose a pet that exists."] }, statusCode: 422);
        }

        if (pet.Status != PetStatus.Available)
        {
            return Failure(409, $"{pet.Name} is not available for adoption.");
        }

        var quantity = int.TryParse(form["quantity"], CultureInfo.InvariantCulture, out var q) ? q : 1;
        pet.Status = PetStatus.Sold;
        return Results.Json(new Order
        {
            PetId = petId,
            Quantity = quantity,
            ShipDate = DateTimeOffset.UtcNow.AddDays(2),
            Complete = true,
        }, Json);
    }

    private static async Task<(T? Value, IResult? Invalid)> ReadJsonAsync<T>(HttpRequest request)
        where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(Json) is { } value
                ? (value, null)
                : (null, Failure(400, "The request body is empty."));
        }
        catch (JsonException ex)
        {
            return (null, Results.Problem(title: "The request body is not valid JSON for this operation.", detail: ex.Message, statusCode: 400));
        }
        catch (InvalidOperationException)
        {
            return (null, Failure(415, "Send the body as JSON."));
        }
    }

    private static void ValidateName(string? name, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Every pet needs a name."];
        }
        else if (name.Trim().Length > 100)
        {
            errors["name"] = ["Keep the name to 100 characters or fewer."];
        }
    }

    private static IResult NotFound(long id) => Failure(404, $"There is no pet {id}.");

    private static IResult Failure(int status, string message) =>
        Results.Json(new Error { Code = status, Message = message }, Json, statusCode: status);
}
