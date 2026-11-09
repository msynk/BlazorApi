using Petstore.Tests.Api;

namespace Petstore.Tests;

/// <summary>Example application logic that depends on the generated client interface.</summary>
public sealed class AdoptionService
{
    private readonly IPetstoreClient _client;

    public AdoptionService(IPetstoreClient client) => _client = client;

    public async Task<string> AdoptAsync(long petId, CancellationToken cancellationToken = default)
    {
        var result = await _client.GetPetResultAsync(petId, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.StatusCode == 404 ? $"Pet {petId} does not exist." : $"The pet store failed: {result.Error.Message}";
        }

        var pet = result.Value!;
        if (pet.Status != PetStatus.Available)
        {
            return $"{pet.Name} is not available.";
        }

        await _client.PatchPetAsync(petId, new PetPatch { Tag = "adopted" }, cancellationToken);
        return $"{pet.Name} has been adopted.";
    }
}
