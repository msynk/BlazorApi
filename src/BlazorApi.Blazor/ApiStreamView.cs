using BlazorApi.Runtime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace BlazorApi.Blazor;

/// <summary>
/// Renders items while they arrive: a streamed response (server-sent events, JSON Lines) from a generated
/// <c>...ResultAsync</c> method through <see cref="Load"/>, or any asynchronous sequence such as a generated page or
/// item walker through <see cref="Items"/>. With <see cref="PageSize"/> it pauses after that many items and shows a
/// "load more" button (infinite lists over paginated operations). It restarts when <see cref="Key"/> changes and
/// stops (releasing the response) when the key changes again or the component is disposed.
/// </summary>
/// <typeparam name="TItem">The item type.</typeparam>
public sealed class ApiStreamView<TItem> : ComponentBase, IAsyncDisposable
{
    private readonly List<TItem> _items = [];
    private CancellationTokenSource? _cancellation;
    private IAsyncEnumerator<TItem>? _enumerator;
    private IAsyncDisposable? _stream;
    private object? _loadedKey;
    private bool _started;
    private long _lastRender;

    /// <summary>Opens a streamed response, for example <c>ct =&gt; Client.StreamEventsResultAsync(ct)</c>.</summary>
    [Parameter]
    public Func<CancellationToken, Task<ApiResult<ApiStream<TItem>>>>? Load { get; set; }

    /// <summary>Produces the items, for example a generated item walker. Use either this or <see cref="Load"/>.</summary>
    [Parameter]
    public Func<CancellationToken, IAsyncEnumerable<TItem>>? Items { get; set; }

    /// <summary>A value that identifies what is loaded; a new value restarts the stream.</summary>
    [Parameter]
    public object? Key { get; set; }

    /// <summary>Rendered for each item (unless <see cref="ChildContent"/> is set).</summary>
    [Parameter]
    public RenderFragment<TItem>? ItemTemplate { get; set; }

    /// <summary>Rendered with every item received so far, for example to join streamed text fragments.</summary>
    [Parameter]
    public RenderFragment<IReadOnlyList<TItem>>? ChildContent { get; set; }

    /// <summary>Rendered before the first item arrives.</summary>
    [Parameter]
    public RenderFragment? Loading { get; set; }

    /// <summary>Rendered when the stream ends without items.</summary>
    [Parameter]
    public RenderFragment? Empty { get; set; }

    /// <summary>Rendered with the error on failure; an <see cref="ApiErrorView"/> with a retry button when null.</summary>
    [Parameter]
    public RenderFragment<ApiError>? Error { get; set; }

    /// <summary>The number of items to read before pausing; <see langword="null"/> (the default) reads everything.</summary>
    [Parameter]
    public int? PageSize { get; set; }

    /// <summary>The text of the button that reads the next <see cref="PageSize"/> items. Default: "Load more".</summary>
    [Parameter]
    public string LoadMoreText { get; set; } = "Load more";

    /// <summary>
    /// The shortest time between two renders while items arrive (fast streams render in batches). Default: 50 ms.
    /// </summary>
    [Parameter]
    public TimeSpan RenderInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The items received so far.</summary>
    public IReadOnlyList<TItem> ReceivedItems => _items;

    /// <summary>Whether items are being read.</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>Whether the stream has ended (all items read, or it failed).</summary>
    public bool IsComplete { get; private set; }

    /// <summary>The failure, when the stream could not be opened or broke.</summary>
    public ApiError? Failure { get; private set; }

    /// <summary>Starts again from the first item.</summary>
    public Task ReloadAsync() => StartAsync();

    /// <summary>Reads the next <see cref="PageSize"/> items (or the rest).</summary>
    public Task LoadMoreAsync() => _enumerator is null || IsStreaming || IsComplete ? Task.CompletedTask : PumpAsync(_cancellation!.Token);

    /// <summary>Stops reading and releases the response; the items received so far stay.</summary>
    public async Task StopAsync()
    {
        await ReleaseAsync().ConfigureAwait(true);
        IsStreaming = false;
        IsComplete = true;
        StateHasChanged();
    }

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        if ((Load is null) == (Items is null))
        {
            throw new InvalidOperationException($"{nameof(ApiStreamView<TItem>)} needs exactly one of '{nameof(Load)}' and '{nameof(Items)}'.");
        }

        if (_started && Equals(_loadedKey, Key))
        {
            return Task.CompletedTask;
        }

        _started = true;
        _loadedKey = Key;
        return StartAsync();
    }

    private async Task StartAsync()
    {
        await ReleaseAsync().ConfigureAwait(true);
        var cancellation = _cancellation = new CancellationTokenSource();
        _items.Clear();
        Failure = null;
        IsComplete = false;
        IsStreaming = true;
        StateHasChanged();
        try
        {
            if (Load is not null)
            {
                var result = await Load(cancellation.Token).ConfigureAwait(true);
                if (cancellation.IsCancellationRequested)
                {
                    result.Value?.Dispose();
                    return;
                }

                if (!result.IsSuccess)
                {
                    Fail(result.Error);
                    return;
                }

                _stream = result.Value;
                _enumerator = result.Value!.GetAsyncEnumerator(cancellation.Token);
            }
            else
            {
                _enumerator = Items!(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (ApiException ex)
        {
            Fail(ex.Error);
            return;
        }

        await PumpAsync(cancellation.Token).ConfigureAwait(true);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var enumerator = _enumerator!;
        IsStreaming = true;
        var read = 0;
        try
        {
            while (PageSize is not { } size || read < size)
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(true))
                {
                    IsComplete = true;
                    await ReleaseEnumeratorAsync().ConfigureAwait(true);
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _items.Add(enumerator.Current);
                read++;
                var now = Environment.TickCount64;
                if (now - _lastRender >= RenderInterval.TotalMilliseconds)
                {
                    _lastRender = now;
                    StateHasChanged();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (ApiException ex)
        {
            Fail(ex.Error);
            await ReleaseEnumeratorAsync().ConfigureAwait(true);
            return;
        }

        IsStreaming = false;
        StateHasChanged();
    }

    private void Fail(ApiError error)
    {
        Failure = error;
        IsStreaming = false;
        IsComplete = true;
        StateHasChanged();
    }

    private async Task ReleaseEnumeratorAsync()
    {
        if (_enumerator is { } enumerator)
        {
            _enumerator = null;
            await enumerator.DisposeAsync().ConfigureAwait(true);
        }

        if (_stream is { } stream)
        {
            _stream = null;
            await stream.DisposeAsync().ConfigureAwait(true);
        }
    }

    private async Task ReleaseAsync()
    {
        if (_cancellation is { } cancellation)
        {
            _cancellation = null;
            await cancellation.CancelAsync().ConfigureAwait(true);
            cancellation.Dispose();
        }

        try
        {
            await ReleaseEnumeratorAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A cancelled enumerator may report its cancellation while it is disposed.
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Failure is { } failure)
        {
            if (Error is not null)
            {
                builder.AddContent(0, Error(failure));
            }
            else
            {
                builder.OpenComponent<ApiErrorView>(1);
                builder.AddComponentParameter(2, nameof(ApiErrorView.Error), failure);
                builder.AddComponentParameter(3, nameof(ApiErrorView.OnRetry), EventCallback.Factory.Create(this, ReloadAsync));
                builder.CloseComponent();
            }

            if (_items.Count == 0)
            {
                return;
            }
        }

        if (_items.Count == 0)
        {
            if (IsComplete && Failure is null)
            {
                builder.AddContent(4, Empty);
            }
            else if (IsStreaming)
            {
                if (Loading is not null)
                {
                    builder.AddContent(5, Loading);
                }
                else
                {
                    builder.OpenElement(6, "div");
                    builder.AddAttribute(7, "class", "api-loading");
                    builder.AddAttribute(8, "aria-busy", "true");
                    builder.AddContent(9, "Loading...");
                    builder.CloseElement();
                }
            }

            return;
        }

        if (ChildContent is not null)
        {
            builder.AddContent(10, ChildContent(_items));
        }
        else if (ItemTemplate is not null)
        {
            foreach (var item in _items)
            {
                builder.AddContent(11, ItemTemplate(item));
            }
        }

        if (!IsComplete && !IsStreaming && _enumerator is not null)
        {
            builder.OpenElement(12, "button");
            builder.AddAttribute(13, "type", "button");
            builder.AddAttribute(14, "class", "api-load-more");
            builder.AddAttribute(15, "onclick", EventCallback.Factory.Create(this, LoadMoreAsync));
            builder.AddContent(16, LoadMoreText);
            builder.CloseElement();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ReleaseAsync().ConfigureAwait(false);
}
