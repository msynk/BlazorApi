using System.Runtime.CompilerServices;

namespace BlazorApi.Runtime;

/// <summary>Settings of <see cref="ApiEventSource.Reconnecting{T}"/>.</summary>
public sealed class ApiReconnectOptions
{
    /// <summary>How many times in a row a broken stream is reopened before the failure is thrown. Default: 5.</summary>
    public int MaxReconnects { get; set; } = 5;

    /// <summary>The wait before reconnecting when the server has not sent a <c>retry:</c> field. Default: 3 s.</summary>
    public TimeSpan DefaultRetry { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether a stream that ends normally is reopened too (an endless event feed). Default: false, so streams that end
    /// by design (a generated answer, an export) are not replayed.
    /// </summary>
    public bool ReconnectWhenEnded { get; set; }

    /// <summary>The clock used for waits (for tests). Default: the system clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Server-sent event streams that survive dropped connections, following the WHATWG EventSource model: the stream is
/// reopened after the server's <c>retry:</c> delay with the last event id, which the server uses to resume.
/// </summary>
public static class ApiEventSource
{
    /// <summary>
    /// Reads events from streams opened by <paramref name="open"/>, reopening it when the connection breaks
    /// (<see cref="ApiNetworkException"/>) and passing the id of the last event received, which the call sends as the
    /// <c>Last-Event-ID</c> header:
    /// <code>
    /// ApiEventSource.Reconnecting&lt;Progress&gt;((lastEventId, ct) =&gt; api
    ///     .WithOptions(o =&gt; { if (lastEventId is not null) o.DefaultHeaders["Last-Event-ID"] = lastEventId; })
    ///     .StreamEventsResultAsync(ct))
    /// </code>
    /// A response that is not a success ends the sequence with its <see cref="ApiException"/> (a server answering
    /// 204 or an error does not want the client to reconnect).
    /// </summary>
    public static async IAsyncEnumerable<ServerSentEvent<T>> Reconnecting<T>(
        Func<string?, CancellationToken, Task<ApiResult<ApiStream<ServerSentEvent<T>>>>> open,
        ApiReconnectOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (open is null)
        {
            throw new ArgumentNullException(nameof(open));
        }

        options ??= new ApiReconnectOptions();
        string? lastEventId = null;
        var retry = options.DefaultRetry;
        var failures = 0;
        while (true)
        {
            var result = await open(lastEventId, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                if (result.Error.Kind is ApiErrorKind.Network or ApiErrorKind.Timeout && failures < options.MaxReconnects)
                {
                    failures++;
                    await Task.Delay(retry, options.TimeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw result.Error.ToException();
            }

            if (result.StatusCode == 204)
            {
                yield break;
            }

            var stream = result.Value!;
            ApiException? broken = null;
            await using (((IAsyncDisposable)stream).ConfigureAwait(false))
            {
                var enumerator = stream.GetAsyncEnumerator(cancellationToken);
                await using (enumerator.ConfigureAwait(false))
                {
                    while (true)
                    {
                        var (hasEvent, failure) = await MoveNextAsync(enumerator).ConfigureAwait(false);
                        if (failure is not null)
                        {
                            broken = failure;
                            break;
                        }

                        if (!hasEvent)
                        {
                            break;
                        }

                        var current = enumerator.Current;
                        failures = 0;
                        lastEventId = current.Id ?? lastEventId;
                        retry = current.Retry ?? retry;
                        yield return current;
                    }
                }
            }

            if (broken is null && !options.ReconnectWhenEnded)
            {
                yield break;
            }

            if (broken is not null && ++failures > options.MaxReconnects)
            {
                throw broken;
            }

            await Task.Delay(retry, options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<(bool HasEvent, ApiException? Failure)> MoveNextAsync<T>(IAsyncEnumerator<T> enumerator)
    {
        try
        {
            return (await enumerator.MoveNextAsync().ConfigureAwait(false), null);
        }
        catch (ApiNetworkException ex)
        {
            return (false, ex);
        }
    }
}
