using System.Net;
using BlazorApi.Runtime;
using BlazorApi.Testing;
using Petstore.Client;

namespace BlazorApi.Integration.Tests;

public class MockingTests
{
    [Fact]
    public async Task Generated_mocks_answer_setups_and_record_calls()
    {
        var mock = new PetstoreClientMock()
            .SetupGetPet(id => PetstoreFixtures.CreateDog(d => d.Id = id))
            .SetupCreatePet(PetstoreFixtures.CreateCat());
        IPetstoreClient client = mock;

        var pet = await client.GetPetAsync(7);
        var created = await client.CreatePetAsync(new NewPet { Name = "Tom", Kind = "cat" });

        Assert.Equal(7, Assert.IsType<Dog>(pet).Id);
        Assert.IsType<Cat>(created);
        mock.Verify(PetstoreClientMock.Operations.GetPet);
        mock.Verify(PetstoreClientMock.Operations.CreatePet, call => call.Argument<NewPet>("body").Name == "Tom");
        mock.VerifyNoOtherCalls(PetstoreClientMock.Operations.GetPet, PetstoreClientMock.Operations.CreatePet);
    }

    [Fact]
    public async Task Mocks_simulate_errors_timeouts_and_delays()
    {
        var mock = new PetstoreClientMock();
        mock.SetupError(PetstoreClientMock.Operations.GetPet, 404, new Error { Code = 404, Message = "missing" });
        mock.SetupTimeout(PetstoreClientMock.Operations.GetHealth);
        mock.SetupDelay(PetstoreClientMock.Operations.ListPets, TimeSpan.FromSeconds(30));

        var notFound = await mock.GetPetResultAsync(1);
        await Assert.ThrowsAsync<ApiTimeoutException>(() => mock.GetHealthAsync());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mock.ListPetsAsync(cancellationToken: cancellation.Token));
        var unconfigured = await mock.GetInventoryResultAsync();

        Assert.Equal("missing", Assert.IsType<Error>(notFound.Error!.Content).Message);
        Assert.Equal(501, unconfigured.StatusCode);
        Assert.Throws<ApiMockVerificationException>(() => mock.Verify(PetstoreClientMock.Operations.GetPet, times: 2));
    }

    [Fact]
    public void Fixtures_build_valid_deterministic_models()
    {
        var first = PetstoreFixtures.CreatePet();
        var second = PetstoreFixtures.CreatePet();
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        Assert.IsType<Dog>(first);
        Assert.Equal(first.Name, second.Name);
        Assert.NotNull(first.Owner!.Manager);
        Assert.True(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(first, new(first), results, validateAllProperties: true));
        Assert.Equal(PetStatus.Available, first.Status);
        Assert.Single(PetstoreFixtures.CreatePetPage().Items);
    }

    [Fact]
    public async Task Mock_http_handler_serves_generated_clients_without_a_server()
    {
        var handler = new MockApiHandler()
            .RespondJson("getPet", PetstoreFixtures.CreateDog(d => d.Name = "Rex"))
            .RespondOnce("listPets", HttpStatusCode.ServiceUnavailable)
            .RespondJson("listPets", new PetPage { Items = [PetstoreFixtures.CreateCat()] })
            .Throw("getHealth", new HttpRequestException("offline"));
        var client = new PetstoreClient(new HttpClient(handler));

        var pet = await client.GetPetAsync(3);
        var unavailable = await client.ListPetsResultAsync();
        var page = await client.ListPetsAsync(limit: 5);
        var offline = await client.GetHealthResultAsync();

        Assert.Equal("Rex", pet.Name);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.IsType<Cat>(Assert.Single(page.Items));
        Assert.Equal(ApiErrorKind.Network, offline.Error!.Kind);
        handler.Verify("listPets", times: 2);
        Assert.Equal("5", System.Web.HttpUtility.ParseQueryString(handler.RequestsTo("listPets")[1].Uri!.Query)["limit"]);
    }
}
