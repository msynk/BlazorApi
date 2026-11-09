using System.Net;
using BlazorApi.OpenApi;
using BlazorApi.OpenApi.Compatibility;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Model;
using BlazorApi.Testing;
using Petstore.Tests.Api;

namespace Petstore.Tests;

public class AdoptionServiceTests
{
    [Fact]
    public async Task Adopts_available_pets_with_a_generated_mock()
    {
        var mock = new PetstoreClientMock()
            .SetupGetPet(id => PetstoreFixtures.CreateDog(dog => { dog.Id = id; dog.Name = "Rex"; dog.Status = PetStatus.Available; }))
            .SetupPatchPet(PetstoreFixtures.CreateDog());

        var message = await new AdoptionService(mock).AdoptAsync(7);

        Assert.Equal("Rex has been adopted.", message);
        mock.Verify(PetstoreClientMock.Operations.PatchPet, call =>
            call.Argument<long>("petId") == 7 && call.Argument<PetPatch>("body").Tag.Value == "adopted");
    }

    [Fact]
    public async Task Reports_missing_pets_from_the_typed_error()
    {
        var mock = new PetstoreClientMock();
        mock.SetupError(PetstoreClientMock.Operations.GetPet, 404, new Error { Code = 404, Message = "missing" });

        var message = await new AdoptionService(mock).AdoptAsync(1);

        Assert.Equal("Pet 1 does not exist.", message);
        mock.VerifyNoOtherCalls(PetstoreClientMock.Operations.GetPet);
    }

    [Fact]
    public async Task Exercises_the_real_client_against_a_mock_handler()
    {
        var handler = new MockApiHandler()
            .RespondJson("getPet", PetstoreFixtures.CreateCat(cat => { cat.Name = "Tom"; cat.Status = PetStatus.Available; }))
            .RespondJson("patchPet", PetstoreFixtures.CreateCat());
        var client = new PetstoreClient(new HttpClient(handler));

        var message = await new AdoptionService(client).AdoptAsync(3);

        Assert.Equal("Tom has been adopted.", message);
        var patch = Assert.Single(handler.RequestsTo("patchPet"));
        Assert.Equal("""{"tag":"adopted"}""", patch.Body);
        Assert.Equal("application/merge-patch+json", patch.Headers["Content-Type"].Split(';')[0]);
    }

    [Fact]
    public async Task Server_failures_do_not_throw_from_the_service()
    {
        var handler = new MockApiHandler().Respond("getPet", HttpStatusCode.ServiceUnavailable);
        var client = new PetstoreClient(new HttpClient(handler));

        var message = await new AdoptionService(client).AdoptAsync(3);

        Assert.StartsWith("The pet store failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_contract_has_no_breaking_changes_since_the_published_baseline()
    {
        static ApiModel Load(string path)
        {
            var result = OpenApiLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "Contracts", path));
            Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
            return ApiModelBuilder.Build(result.Document!, new DiagnosticBag());
        }

        var report = ContractComparer.Compare(Load("baseline/petstore.yaml"), Load("petstore.yaml"));

        Assert.False(report.HasChangesAtLeast(ChangeSeverity.Breaking), report.ToText());
    }
}
