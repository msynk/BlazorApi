using System.Collections.Concurrent;

namespace Petstore.BlazorApp.Api;

/// <summary>
/// The in-memory data behind the sample API, so the sample runs without external services. It stores the generated
/// models directly, which keeps server and client on the same contract. Data resets when the app restarts.
/// </summary>
public sealed class PetStore
{
    /// <summary>The bearer token the API accepts for the <c>bearerAuth</c> scheme.</summary>
    public const string DemoToken = "demo-token";

    /// <summary>The key the API accepts for the <c>apiKey</c> scheme.</summary>
    public const string DemoApiKey = "demo-key";

    private readonly ConcurrentDictionary<long, Pet> _pets = new();
    private readonly ConcurrentDictionary<long, StoredPhoto> _photos = new();
    private long _nextId;

    public PetStore()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var owner = new Owner { Id = Guid.Parse("6f1c2a52-8d0e-4c3b-9a51-0b7c2f5e9d11"), Name = "Ada Lovelace", Email = "ada@example.com" };

        Add(new Dog { Name = "Rex", Status = PetStatus.Available, BarkVolume = 7, Tag = "good boy", BirthDate = today.AddYears(-3) });
        Add(new Cat { Name = "Luna", Indoor = true, Status = PetStatus.Pending, Tag = "shy", BirthDate = today.AddYears(-2) });
        Add(new Dog { Name = "Fido", Status = PetStatus.Sold, Tag = "senior", BarkVolume = 4.5, BirthDate = today.AddYears(-11), Owner = owner });
        Add(new Cat { Name = "Milo", Indoor = false, Status = PetStatus.Available, BirthDate = today.AddMonths(-8) });
        Add(new Dog { Name = "Bella", Status = PetStatus.Available, BarkVolume = 9.5, Tag = "energetic" });
        Add(new Cat { Name = "Simba", Indoor = true, Status = PetStatus.Available, Tag = "lap cat", BirthDate = today.AddYears(-5) });
        Add(new Dog { Name = "Max", Status = PetStatus.Pending, BarkVolume = 6 });
        Add(new Cat { Name = "Nala", Indoor = true, Status = PetStatus.Sold, Owner = owner });
        Add(new Dog { Name = "Rocky", Status = PetStatus.Available, BarkVolume = 8, BirthDate = today.AddYears(-1) });
        Add(new Cat { Name = "Coco", Indoor = false, Status = PetStatus.Available, Tag = "adventurous" });
        Add(new Dog { Name = "Daisy", Status = PetStatus.Available, BarkVolume = 3, Tag = "quiet" });
        Add(new Cat { Name = "Oliver", Indoor = true, Status = PetStatus.Pending });
        Add(new Dog { Name = "Charlie", Status = PetStatus.Available, BarkVolume = 5.5, BirthDate = today.AddYears(-6) });
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

    /// <summary>Replaces a pet (keeping its server-owned id and creation time); false when it does not exist.</summary>
    public bool Replace(long id, Pet pet)
    {
        if (Find(id) is not { } existing)
        {
            return false;
        }

        pet.Id = id;
        pet.CreatedAt = existing.CreatedAt;
        _pets[id] = pet;
        return true;
    }

    public bool Remove(long id)
    {
        _photos.TryRemove(id, out _);
        return _pets.TryRemove(id, out _);
    }

    public void SetPhoto(long id, StoredPhoto photo) => _photos[id] = photo;

    public StoredPhoto? FindPhoto(long id) => _photos.TryGetValue(id, out var photo) ? photo : null;
}

/// <summary>An uploaded pet photo.</summary>
public sealed record StoredPhoto(byte[] Content, string ContentType, string? Caption);
