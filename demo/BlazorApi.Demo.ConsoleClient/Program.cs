using BlazorApi.Runtime;
using BlazorApi.Runtime.Security;
using Petstore.ConsoleClient.Api;

// The base address of the API hosted by samples/Petstore.BlazorApp (override with the first argument).
var baseAddress = new Uri(args.Length > 0 ? args[0] : "http://localhost:5080/v1/");

// Authentication is a message handler: it adds the token only to operations whose contract requires bearerAuth.
using var http = new HttpClient(new BearerTokenHandler(new StaticAccessTokenProvider("demo-token"), "bearerAuth")
{
    InnerHandler = new HttpClientHandler(),
});
var client = new PetstoreClient(http, new ApiClientOptions { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) });

// Result methods never throw for API failures: check IsSuccess or Error.
var health = await client.GetHealthResultAsync();
if (!health.IsSuccess)
{
    Console.Error.WriteLine($"The API is not reachable at {baseAddress} ({health.Error.Kind}: {health.Error.Message}).");
    Console.Error.WriteLine("Start it with: dotnet run --project samples/Petstore.BlazorApp");
    return 1;
}

Console.WriteLine($"API health: {health.Value}");

// Pagination helpers follow the API's cursors; polymorphic models come back as their concrete types.
Console.WriteLine("Pets:");
await foreach (var pet in client.ListPetsItemsAsync(limit: 4))
{
    var details = pet switch
    {
        Dog dog => $"dog, bark volume {dog.BarkVolume?.ToString() ?? "unknown"}",
        Cat cat => cat.Indoor ? "indoor cat" : "outdoor cat",
        _ => pet.Kind,
    };
    Console.WriteLine($"  #{pet.Id} {pet.Name} ({details})");
}

// Value methods throw ApiException with the typed error body declared by the contract.
var created = await client.CreatePetAsync(new NewPet { Name = "Bolt", Kind = "dog", Status = PetStatus.Available });
Console.WriteLine($"Created #{created.Id} {created.Name}.");

try
{
    await client.CreatePetAsync(new NewPet { Name = string.Empty, Kind = "parrot" });
}
catch (ApiException ex) when (ex.Content is ProblemDetails problem)
{
    Console.WriteLine($"Rejected ({ex.StatusCode}): {problem.Title}");
    foreach (var (field, messages) in problem.Errors ?? new Dictionary<string, IReadOnlyList<string>>())
    {
        Console.WriteLine($"  {field}: {string.Join(" ", messages)}");
    }
}

var missing = await client.GetPetResultAsync(9999);
if (missing.Error?.Content is Error error)
{
    Console.WriteLine($"Pet 9999: {error.Code} {error.Message}");
}

return 0;
