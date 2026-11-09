using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BlazorApi.Runtime.Http;

/// <summary>
/// Sends the requests of a generated client and turns responses into <see cref="ApiResult{T}"/> values. Network
/// failures, timeouts, undeclared or error status codes and unreadable bodies become <see cref="ApiError"/>s;
/// cancellation requested by the caller is the only exception that escapes.
/// </summary>
public sealed class ApiConnection
{
    private readonly Uri? _defaultBaseAddress;

    /// <summary>Creates a connection.</summary>
    /// <param name="httpClient">The HTTP client (from <c>IHttpClientFactory</c> or created by the caller).</param>
    /// <param name="options">Client options; defaults when <see langword="null"/>.</param>
    /// <param name="defaultBaseAddress">The base address from the OpenAPI document, used when no other is configured.</param>
    public ApiConnection(HttpClient httpClient, ApiClientOptions? options = null, string? defaultBaseAddress = null)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Options = options ?? new ApiClientOptions();
        if (!string.IsNullOrEmpty(defaultBaseAddress) && Uri.TryCreate(defaultBaseAddress, UriKind.Absolute, out var uri))
        {
            _defaultBaseAddress = uri;
        }
    }

    /// <summary>The HTTP client.</summary>
    public HttpClient HttpClient { get; }

    /// <summary>The client options.</summary>
    public ApiClientOptions Options { get; }

    /// <summary>The JSON options.</summary>
    public JsonSerializerOptions JsonOptions => Options.JsonSerializerOptions;

    /// <summary>The effective base address.</summary>
    public Uri? BaseAddress => Options.BaseAddress ?? HttpClient.BaseAddress ?? _defaultBaseAddress;

    /// <summary>Sends a request and interprets the response.</summary>
    public async Task<ApiResult<T>> SendAsync<T>(ApiRequestBuilder builder, ApiResponseHandler<T> handler, CancellationToken cancellationToken)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        using var timeout = Options.Timeout is { } limit ? new CancellationTokenSource(limit) : null;
        using var linked = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked?.Token ?? cancellationToken;

        using var request = builder.Build(BaseAddress);
        foreach (var header in Options.DefaultHeaders)
        {
            if (!request.Headers.Contains(header.Key))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        HttpResponseMessage? response = null;
        var disposeResponse = true;
        var stage = "sending the request";
        try
        {
            if (Options.OnRequest is { } onRequest)
            {
                await onRequest(request, token).ConfigureAwait(false);
            }

            response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (Options.OnResponse is { } onResponse)
            {
                await onResponse(response, token).ConfigureAwait(false);
            }

            var status = (int)response.StatusCode;
            var entry = handler.Match(status);
            if (entry.IsSuccess)
            {
                var headers = ApiHeaders.From(response);
                T? value = default;
                if (entry.SuccessReader is { } reader && HasBody(builder, response))
                {
                    stage = "reading the response body";
                    try
                    {
                        value = await reader(response, JsonOptions, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException or InvalidOperationException)
                    {
                        return ApiResult<T>.Failure(new ApiError(ApiErrorKind.Deserialization,
                            $"The {status} response of '{builder.OperationId}' could not be read as {typeof(T).Name}: {ex.Message}")
                        {
                            StatusCode = status,
                            ReasonPhrase = response.ReasonPhrase,
                            Headers = headers,
                            Exception = ex,
                            OperationId = builder.OperationId,
                        });
                    }

                    disposeResponse = !entry.OwnsResponse;
                }

                return ApiResult<T>.Success(response.StatusCode, headers, value);
            }

            stage = "reading the error response body";
            return ApiResult<T>.Failure(await CreateHttpErrorAsync(builder, response, entry.ErrorReader, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && (timeout?.IsCancellationRequested == true || ex.InnerException is TimeoutException))
        {
            var after = Options.Timeout ?? HttpClient.Timeout;
            return ApiResult<T>.Failure(new ApiError(ApiErrorKind.Timeout,
                $"'{builder.OperationId}' timed out after {after.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s while {stage}")
            {
                StatusCode = response is null ? null : (int)response.StatusCode,
                Exception = ex,
                OperationId = builder.OperationId,
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return ApiResult<T>.Failure(new ApiError(ApiErrorKind.Network, $"'{builder.OperationId}' failed while {stage}: {ex.Message}")
            {
                StatusCode = response is null ? null : (int)response.StatusCode,
                Exception = ex,
                OperationId = builder.OperationId,
            });
        }
        finally
        {
            if (disposeResponse)
            {
                response?.Dispose();
            }
        }
    }

    private async Task<ApiError> CreateHttpErrorAsync(ApiRequestBuilder builder, HttpResponseMessage response, ApiErrorReader? reader, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var (body, truncated) = await ReadLimitedAsync(response, Options.MaxErrorBodyLength, cancellationToken).ConfigureAwait(false);
        object? content = null;
        if (!truncated && body.Length > 0)
        {
            reader ??= IsProblemJson(response) ? ApiBody.ProblemError : null;
            if (reader is not null)
            {
                try
                {
                    content = reader(body, JsonOptions);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    // The body does not match its declared type; the raw text stays available in Body.
                }
            }
        }

        var detail = content is ApiProblemDetails problem && (problem.Title ?? problem.Detail) is { } title ? ": " + title : string.Empty;
        return new ApiError(ApiErrorKind.Http,
            $"'{builder.OperationId}' failed with status {status} ({response.ReasonPhrase}){detail}")
        {
            StatusCode = status,
            ReasonPhrase = response.ReasonPhrase,
            Headers = ApiHeaders.From(response),
            Body = body,
            Content = content,
            OperationId = builder.OperationId,
        };
    }

    /// <summary>Whether a success response can carry a body (204, 205, 304 and HEAD responses never do; an explicit empty length does not).</summary>
    private static bool HasBody(ApiRequestBuilder builder, HttpResponseMessage response) =>
        builder.Method != HttpMethod.Head &&
        response.StatusCode is not (System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.ResetContent or System.Net.HttpStatusCode.NotModified) &&
        response.Content.Headers.ContentLength != 0;

    private static bool IsProblemJson(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string Body, bool Truncated)> ReadLimitedAsync(HttpResponseMessage response, int maxLength, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var charset = response.Content.Headers.ContentType?.CharSet;
            Encoding encoding;
            try
            {
                encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }

            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[Math.Min(Math.Max(maxLength, 0) + 1, 81920)];
            var builder = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                builder.Append(buffer, 0, read);
                if (builder.Length > maxLength)
                {
                    builder.Length = maxLength;
                    return (builder.ToString(), true);
                }
            }

            return (builder.ToString(), false);
        }
    }
}
