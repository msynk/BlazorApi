using BlazorApi.Runtime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorApi.Blazor.Tests;

public class ComponentTests
{
    private sealed class Pet
    {
        public string Name { get; set; } = string.Empty;

        public Owner? Owner { get; set; }
    }

    private sealed class Owner
    {
        public string? Email { get; set; }
    }

    private static async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            await output.QuiescenceTask;
            return output.ToHtmlString();
        });
    }

    [Fact]
    public async Task Result_view_renders_the_loaded_value()
    {
        RenderFragment<Pet> content = pet => builder => builder.AddContent(0, "Hello " + pet.Name);
        Func<CancellationToken, Task<ApiResult<Pet>>> load = async ct =>
        {
            await Task.Yield();
            return ApiResults.Success(new Pet { Name = "Rex" });
        };

        var html = await RenderAsync<ApiResultView<Pet>>(new()
        {
            [nameof(ApiResultView<Pet>.Load)] = load,
            [nameof(ApiResultView<Pet>.ChildContent)] = content,
        });

        Assert.Equal("Hello Rex", html);
    }

    [Fact]
    public async Task Result_view_renders_errors_with_problem_details_and_retry()
    {
        var error = ApiError.Http(422, new ApiProblemDetails
        {
            Title = "Validation failed",
            Detail = "Fix the fields.",
            Errors = new Dictionary<string, string[]> { ["name"] = ["Name is required."] },
        });
        Func<CancellationToken, Task<ApiResult<Pet>>> load = _ => Task.FromResult(ApiResults.Failure<Pet>(error));

        var html = await RenderAsync<ApiResultView<Pet>>(new() { [nameof(ApiResultView<Pet>.Load)] = load });

        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Validation failed</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<li>name: Name is required.</li>", html, StringComparison.Ordinal);
        Assert.Contains("Try again</button>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_view_encodes_server_text()
    {
        var html = await RenderAsync<ApiErrorView>(new()
        {
            [nameof(ApiErrorView.Error)] = ApiError.Http(400, body: """{"title":"<script>alert(1)</script>"}"""),
        });

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(404, null, "Not found.")]
    [InlineData(500, null, "The service failed to process the request.")]
    [InlineData(400, """{"message":"Bad id"}""", "Bad id")]
    public void Describes_errors(int status, string? body, string expected)
    {
        Assert.Equal(expected, ApiValidation.Describe(ApiError.Http(status, body: body)).Title);
    }

    [Fact]
    public void Reads_field_errors_from_typed_or_raw_problem_details()
    {
        var raw = ApiError.Http(400, body: """{"errors":{"Name":["Too short"],"owner.email":"Invalid"}}""");

        var fields = ApiValidation.GetFieldErrors(raw);

        Assert.Equal(["Too short"], fields["name"]);
        Assert.Equal(["Invalid"], fields["owner.email"]);
        Assert.Empty(ApiValidation.GetFieldErrors(ApiError.Http(400, body: "not json")));
    }

    [Fact]
    public void Maps_server_errors_to_edit_context_fields()
    {
        var model = new Pet { Owner = new Owner() };
        var context = new EditContext(model);
        var store = new ValidationMessageStore(context);
        var error = ApiError.Http(400, body: """{"errors":{"name":["Too short"],"$.owner.email":["Invalid"],"unknown":["Server says no"]}}""");

        ApiValidationErrors.Apply(context, store, error);

        Assert.Equal(["Too short"], context.GetValidationMessages(context.Field(nameof(Pet.Name))));
        Assert.Equal(["Invalid"], context.GetValidationMessages(new FieldIdentifier(model.Owner, nameof(Owner.Email))));
        Assert.Contains("Server says no", context.GetValidationMessages());
    }
}
