using BlazorApi.Runtime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace BlazorApi.Blazor;

/// <summary>
/// Loads a value with a generated <c>...ResultAsync</c> method and renders the loading, success or error state. It
/// reloads when <see cref="Key"/> changes and cancels a pending load when it changes again or the component is
/// disposed. For caching, deduplication and background refetching use the generated dotnet-query definitions instead.
/// </summary>
/// <typeparam name="TValue">The response body type.</typeparam>
public sealed class ApiResultView<TValue> : ComponentBase, IDisposable
{
    private CancellationTokenSource? _cancellation;
    private object? _loadedKey;
    private bool _loaded;

    /// <summary>Loads the result, for example <c>ct =&gt; Client.GetPetResultAsync(Id, ct)</c>.</summary>
    [Parameter]
    [EditorRequired]
    public Func<CancellationToken, Task<ApiResult<TValue>>> Load { get; set; } = null!;

    /// <summary>A value that identifies what is loaded (for example an id); a new value triggers a reload.</summary>
    [Parameter]
    public object? Key { get; set; }

    /// <summary>Rendered while loading (and while reloading, unless <see cref="KeepPreviousValue"/> is set).</summary>
    [Parameter]
    public RenderFragment? Loading { get; set; }

    /// <summary>Rendered with the value on success.</summary>
    [Parameter]
    public RenderFragment<TValue>? ChildContent { get; set; }

    /// <summary>Rendered with the error on failure; an <see cref="ApiErrorView"/> with a retry button when null.</summary>
    [Parameter]
    public RenderFragment<ApiError>? Error { get; set; }

    /// <summary>Whether to keep showing the previous value while a reload is in progress.</summary>
    [Parameter]
    public bool KeepPreviousValue { get; set; }

    /// <summary>The latest result.</summary>
    public ApiResult<TValue>? Result { get; private set; }

    /// <summary>Whether a load is in progress.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Loads again.</summary>
    public Task ReloadAsync() => LoadAsync();

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        if (_loaded && Equals(_loadedKey, Key))
        {
            return Task.CompletedTask;
        }

        _loaded = true;
        _loadedKey = Key;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        var cancellation = _cancellation = new CancellationTokenSource();
        IsLoading = true;
        if (!KeepPreviousValue)
        {
            Result = null;
        }

        StateHasChanged();
        try
        {
            var result = await Load(cancellation.Token).ConfigureAwait(true);
            if (!cancellation.IsCancellationRequested)
            {
                Result = result;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(cancellation, _cancellation))
            {
                IsLoading = false;
            }
        }

        StateHasChanged();
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Result is null || (IsLoading && !KeepPreviousValue))
        {
            if (Loading is not null)
            {
                builder.AddContent(0, Loading);
            }
            else
            {
                builder.OpenElement(1, "div");
                builder.AddAttribute(2, "class", "api-loading");
                builder.AddAttribute(3, "aria-busy", "true");
                builder.AddContent(4, "Loading...");
                builder.CloseElement();
            }

            return;
        }

        if (!Result.IsSuccess)
        {
            if (Error is not null)
            {
                builder.AddContent(5, Error(Result.Error));
            }
            else
            {
                builder.OpenComponent<ApiErrorView>(6);
                builder.AddComponentParameter(7, nameof(ApiErrorView.Error), Result.Error);
                builder.AddComponentParameter(8, nameof(ApiErrorView.OnRetry), EventCallback.Factory.Create(this, ReloadAsync));
                builder.CloseComponent();
            }

            return;
        }

        if (ChildContent is not null)
        {
            builder.AddContent(9, ChildContent(Result.Value!));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
