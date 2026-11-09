namespace BlazorApi.Runtime;

/// <summary>
/// Thrown by generated client methods when a call fails. <see cref="Error"/> has the details, including the typed
/// error body the OpenAPI document declares for the status code.
/// </summary>
public class ApiException : Exception
{
    /// <summary>Creates an exception for an error.</summary>
    public ApiException(ApiError error)
        : base(error?.Message, error?.Exception) => Error = error ?? throw new ArgumentNullException(nameof(error));

    /// <summary>The error.</summary>
    public ApiError Error { get; }

    /// <summary>The HTTP status code, when a response was received.</summary>
    public int? StatusCode => Error.StatusCode;

    /// <summary>The typed error body, when declared and readable.</summary>
    public object? Content => Error.Content;

    /// <summary>The problem details body, when the error is one.</summary>
    public ApiProblemDetails? Problem => Error.Problem;
}

/// <summary>Thrown when the request could not be sent or the connection failed.</summary>
public sealed class ApiNetworkException : ApiException
{
    /// <summary>Creates the exception.</summary>
    public ApiNetworkException(ApiError error)
        : base(error)
    {
    }
}

/// <summary>Thrown when a request exceeded its timeout.</summary>
public sealed class ApiTimeoutException : ApiException
{
    /// <summary>Creates the exception.</summary>
    public ApiTimeoutException(ApiError error)
        : base(error)
    {
    }
}

/// <summary>Thrown when a success response body could not be read as the declared type.</summary>
public sealed class ApiDeserializationException : ApiException
{
    /// <summary>Creates the exception.</summary>
    public ApiDeserializationException(ApiError error)
        : base(error)
    {
    }
}
