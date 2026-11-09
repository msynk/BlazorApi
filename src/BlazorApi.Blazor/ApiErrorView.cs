using BlazorApi.Runtime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace BlazorApi.Blazor;

/// <summary>
/// Shows an API error: its title, detail and field errors (from problem details), with an optional retry button.
/// Renders nothing when there is no error. Messages are the server's text, rendered as text (never as markup).
/// </summary>
public sealed class ApiErrorView : ComponentBase
{
    /// <summary>The error to show.</summary>
    [Parameter]
    public ApiError? Error { get; set; }

    /// <summary>An exception to show (its <see cref="ApiException.Error"/> when it is an API exception).</summary>
    [Parameter]
    public Exception? Exception { get; set; }

    /// <summary>Called when the retry button is clicked; no button is shown when null.</summary>
    [Parameter]
    public EventCallback OnRetry { get; set; }

    /// <summary>The retry button text.</summary>
    [Parameter]
    public string RetryText { get; set; } = "Try again";

    /// <summary>The CSS class of the container.</summary>
    [Parameter]
    public string CssClass { get; set; } = "api-error";

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var error = Error ?? (Exception as ApiException)?.Error;
        if (error is null && Exception is null)
        {
            return;
        }

        var (title, detail) = error is null ? ("Something went wrong.", Exception!.Message) : ApiValidation.Describe(error);
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", CssClass);
        builder.AddAttribute(2, "role", "alert");
        builder.OpenElement(3, "strong");
        builder.AddContent(4, title);
        builder.CloseElement();
        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.OpenElement(5, "p");
            builder.AddContent(6, detail);
            builder.CloseElement();
        }

        var fields = ApiValidation.GetFieldErrors(error);
        if (fields.Count > 0)
        {
            builder.OpenElement(7, "ul");
            foreach (var field in fields)
            {
                foreach (var message in field.Value)
                {
                    builder.OpenElement(8, "li");
                    builder.AddContent(9, field.Key.Length == 0 ? message : field.Key + ": " + message);
                    builder.CloseElement();
                }
            }

            builder.CloseElement();
        }

        if (OnRetry.HasDelegate)
        {
            builder.OpenElement(10, "button");
            builder.AddAttribute(11, "type", "button");
            builder.AddAttribute(12, "onclick", EventCallback.Factory.Create(this, () => OnRetry.InvokeAsync()));
            builder.AddContent(13, RetryText);
            builder.CloseElement();
        }

        builder.CloseElement();
    }
}
