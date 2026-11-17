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

    private static async IAsyncEnumerable<int> Numbers(int count, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= count; i++)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
        }
    }

    [Fact]
    public async Task Stream_view_renders_streamed_items()
    {
        RenderFragment<IReadOnlyList<string>> content = items => builder => builder.AddContent(0, string.Concat(items));
        Func<CancellationToken, Task<ApiResult<ApiStream<string>>>> load = _ =>
            Task.FromResult(ApiResults.Success(ApiStream.From(["Hel", "lo", "!"])));

        var html = await RenderAsync<ApiStreamView<string>>(new()
        {
            [nameof(ApiStreamView<string>.Load)] = load,
            [nameof(ApiStreamView<string>.ChildContent)] = content,
        });

        Assert.Equal("Hello!", html);
    }

    [Fact]
    public async Task Stream_view_pages_through_walkers_with_a_load_more_button()
    {
        RenderFragment<int> item = n => builder => builder.AddContent(0, "[" + n + "]");
        Func<CancellationToken, IAsyncEnumerable<int>> items = ct => Numbers(5, ct);

        var html = await RenderAsync<ApiStreamView<int>>(new()
        {
            [nameof(ApiStreamView<int>.Items)] = items,
            [nameof(ApiStreamView<int>.ItemTemplate)] = item,
            [nameof(ApiStreamView<int>.PageSize)] = 2,
        });

        Assert.StartsWith("[1][2]<button", html, StringComparison.Ordinal);
        Assert.Contains("api-load-more", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[3]", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_view_shows_errors_and_empty_streams()
    {
        Func<CancellationToken, Task<ApiResult<ApiStream<int>>>> failing = _ =>
            Task.FromResult(ApiResults.Failure<ApiStream<int>>(ApiError.Http(503)));
        Func<CancellationToken, IAsyncEnumerable<int>> none = ct => Numbers(0, ct);
        RenderFragment empty = builder => builder.AddContent(0, "Nothing yet");

        var error = await RenderAsync<ApiStreamView<int>>(new() { [nameof(ApiStreamView<int>.Load)] = failing });
        var nothing = await RenderAsync<ApiStreamView<int>>(new()
        {
            [nameof(ApiStreamView<int>.Items)] = none,
            [nameof(ApiStreamView<int>.Empty)] = empty,
        });

        Assert.Contains("role=\"alert\"", error, StringComparison.Ordinal);
        Assert.Equal("Nothing yet", nothing);
    }
}
