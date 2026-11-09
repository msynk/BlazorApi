using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace BlazorApi.Runtime;

/// <summary>The kind of an <see cref="ApiError"/>.</summary>
public enum ApiErrorKind
{
    /// <summary>The server answered with a status code the operation does not define as success.</summary>
    Http,

    /// <summary>The request did not reach the server or the connection failed (DNS, TLS, reset, ...).</summary>
    Network,

    /// <summary>The request or the response body exceeded the configured timeout.</summary>
    Timeout,

    /// <summary>A success response body could not be read as the declared type.</summary>
    Deserialization,
}

/// <summary>Why an API call failed.</summary>
public sealed class ApiError
{
    internal ApiError(ApiErrorKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>The kind of failure.</summary>
    public ApiErrorKind Kind { get; }

    /// <summary>A description of the failure (never includes credentials).</summary>
    public string Message { get; }

    /// <summary>The HTTP status code, when a response was received.</summary>
    public int? StatusCode { get; internal init; }

    /// <summary>The reason phrase, when a response was received.</summary>
    public string? ReasonPhrase { get; internal init; }

    /// <summary>The response headers (response and content headers), when a response was received.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; internal init; } = ApiHeaders.Empty;

    /// <summary>The response body text (truncated to <see cref="ApiClientOptions.MaxErrorBodyLength"/>), when read.</summary>
    public string? Body { get; internal init; }

    /// <summary>
    /// The error body deserialized as the type the OpenAPI document declares for this status code (for example a
    /// generated <c>Error</c> model or <see cref="ApiProblemDetails"/>), when declared and readable.
    /// </summary>
    public object? Content { get; internal init; }

    /// <summary>The underlying exception, for network, timeout and deserialization failures.</summary>
    public Exception? Exception { get; internal init; }

    /// <summary>The operation that failed.</summary>
    public string? OperationId { get; internal init; }

    /// <summary>The <see cref="Content"/> as RFC 9457 problem details, when it is one.</summary>
    public ApiProblemDetails? Problem => Content as ApiProblemDetails;

    /// <summary>Gets the typed error content.</summary>
    public bool TryGetContent<T>([NotNullWhen(true)] out T? content)
    {
        if (Content is T typed)
        {
            content = typed;
            return true;
        }

        content = default;
        return false;
    }

    /// <summary>Creates an HTTP error (for mocks and tests).</summary>
    public static ApiError Http(int statusCode, object? content = null, string? body = null, string? operationId = null) =>
        new(ApiErrorKind.Http, $"'{operationId ?? "operation"}' failed with status {statusCode}")
        {
            StatusCode = statusCode,
            Content = content,
            Body = body,
            OperationId = operationId,
        };

    /// <summary>Creates a network error (for mocks and tests).</summary>
    public static ApiError Network(Exception exception, string? operationId = null) =>
        new(ApiErrorKind.Network, $"'{operationId ?? "operation"}' failed: {exception?.Message}") { Exception = exception, OperationId = operationId };

    /// <summary>Creates a timeout error (for mocks and tests).</summary>
    public static ApiError Timeout(string? operationId = null) =>
        new(ApiErrorKind.Timeout, $"'{operationId ?? "operation"}' timed out") { Exception = new TimeoutException(), OperationId = operationId };

    /// <summary>Creates the exception that represents this error.</summary>
    public ApiException ToException() => Kind switch
    {
        ApiErrorKind.Network => new ApiNetworkException(this),
        ApiErrorKind.Timeout => new ApiTimeoutException(this),
        ApiErrorKind.Deserialization => new ApiDeserializationException(this),
        _ => new ApiException(this),
    };

    /// <inheritdoc />
    public override string ToString() => Message;
}

/// <summary>The outcome of an API call without a response body.</summary>
public class ApiResult
{
    internal ApiResult(int statusCode, IReadOnlyDictionary<string, IReadOnlyList<string>> headers, ApiError? error)
    {
        StatusCode = statusCode;
        Headers = headers;
        Error = error;
    }

    /// <summary>Whether the call succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>The HTTP status code, or 0 when no response was received.</summary>
    public int StatusCode { get; }

    /// <summary>The response headers (response and content headers).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <summary>The error, when the call failed.</summary>
    public ApiError? Error { get; }

    /// <summary>Throws the corresponding <see cref="ApiException"/> when the call failed.</summary>
    public void EnsureSuccess()
    {
        if (Error is not null)
        {
            throw Error.ToException();
        }
    }

    /// <summary>Gets the first value of a response header.</summary>
    public string? GetHeader(string name) =>
        Headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    internal static ApiResult Success(HttpStatusCode status, IReadOnlyDictionary<string, IReadOnlyList<string>> headers) =>
        new((int)status, headers, null);

    internal static ApiResult Failure(ApiError error) =>
        new(error.StatusCode ?? 0, error.Headers, error);
}

/// <summary>The outcome of an API call with a typed response body.</summary>
/// <typeparam name="T">The body type of the success response.</typeparam>
public sealed class ApiResult<T> : ApiResult
{
    private readonly T? _value;

    internal ApiResult(int statusCode, IReadOnlyDictionary<string, IReadOnlyList<string>> headers, T? value, ApiError? error)
        : base(statusCode, headers, error) => _value = value;

    /// <summary>
    /// The response body. It is the default value for success responses the operation declares without a body
    /// (for example 204) and for failures.
    /// </summary>
    public T? Value => _value;

    /// <summary>Returns the value or throws the corresponding <see cref="ApiException"/>.</summary>
    public T GetValueOrThrow()
    {
        EnsureSuccess();
        return _value!;
    }

    internal static ApiResult<T> Success(HttpStatusCode status, IReadOnlyDictionary<string, IReadOnlyList<string>> headers, T? value) =>
        new((int)status, headers, value, null);

    internal static new ApiResult<T> Failure(ApiError error) =>
        new(error.StatusCode ?? 0, error.Headers, default, error);
}

/// <summary>Creates results directly (for mocks, tests and hand-written clients).</summary>
public static class ApiResults
{
    /// <summary>A success with a value.</summary>
    public static ApiResult<T> Success<T>(T value, int statusCode = 200, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null) =>
        new(statusCode, headers ?? ApiHeaders.Empty, value, null);

    /// <summary>A success without a body.</summary>
    public static ApiResult NoContent(int statusCode = 204, IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null) =>
        new(statusCode, headers ?? ApiHeaders.Empty, null);

    /// <summary>A failure.</summary>
    public static ApiResult<T> Failure<T>(ApiError error) => ApiResult<T>.Failure(error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>A failure without a body type.</summary>
    public static ApiResult Failure(ApiError error) => ApiResult.Failure(error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Response header helpers.</summary>
internal static class ApiHeaders
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Empty =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> From(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        return headers;
    }
}
