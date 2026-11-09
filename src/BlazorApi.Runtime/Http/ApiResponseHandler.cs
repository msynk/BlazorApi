using System.Text.Json;

namespace BlazorApi.Runtime.Http;

/// <summary>Reads a success response body.</summary>
public delegate ValueTask<T> ApiBodyReader<T>(HttpResponseMessage response, JsonSerializerOptions options, CancellationToken cancellationToken);

/// <summary>Reads an error response body (already buffered as text) into its declared type.</summary>
public delegate object? ApiErrorReader(string body, JsonSerializerOptions options);

/// <summary>A response type for operations without a response body.</summary>
public readonly struct ApiNoContent
{
}

/// <summary>
/// Describes how an operation's responses are interpreted: which status codes are success and how each body is
/// read. Generated clients create one static instance per operation.
/// </summary>
/// <typeparam name="T">The success body type (<see cref="ApiNoContent"/> when there is none).</typeparam>
public sealed class ApiResponseHandler<T>
{
    private readonly List<Entry> _entries = [];

    /// <summary>Declares a success status (<c>200</c>, <c>2XX</c> or <c>default</c>) and how its body is read.</summary>
    /// <param name="status">The status key.</param>
    /// <param name="reader">The body reader, or <see langword="null"/> when the response has no body.</param>
    /// <param name="ownsResponse">Whether the value keeps the response alive (streamed files).</param>
    public ApiResponseHandler<T> Success(string status, ApiBodyReader<T>? reader = null, bool ownsResponse = false)
    {
        _entries.Add(new Entry(status, isSuccess: true, reader, null, ownsResponse));
        return this;
    }

    /// <summary>Declares an error status (<c>404</c>, <c>4XX</c> or <c>default</c>) and how its body is read.</summary>
    public ApiResponseHandler<T> Error(string status, ApiErrorReader? reader = null)
    {
        _entries.Add(new Entry(status, isSuccess: false, null, reader, ownsResponse: false));
        return this;
    }

    internal Entry Match(int status)
    {
        Entry? range = null;
        Entry? fallback = null;
        var statusText = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var entry in _entries)
        {
            if (entry.Status == statusText)
            {
                return entry;
            }

            if (entry.Status.Length == 3 && entry.Status[1] is 'X' or 'x' && entry.Status[0] == statusText[0])
            {
                range ??= entry;
            }
            else if (entry.Status == "default")
            {
                fallback ??= entry;
            }
        }

        if (range is not null)
        {
            return range;
        }

        var isSuccessCode = status is >= 200 and < 300;
        // 'default' covers the remaining codes, but an error status never counts as success: when 'default' is the
        // operation's success response (no 2xx declared), other error codes are errors without a typed body.
        if (fallback is not null && fallback.IsSuccess == isSuccessCode)
        {
            return fallback;
        }

        if (isSuccessCode)
        {
            // An undeclared 2xx status: read it like a declared success with a body, if there is one.
            return _entries.FirstOrDefault(e => e.IsSuccess && e.SuccessReader is not null) ?? new Entry(statusText, true, null, null, false);
        }

        return new Entry(statusText, isSuccess: false, null, null, ownsResponse: false);
    }

    internal sealed class Entry
    {
        public Entry(string status, bool isSuccess, ApiBodyReader<T>? successReader, ApiErrorReader? errorReader, bool ownsResponse)
        {
            Status = status;
            IsSuccess = isSuccess;
            SuccessReader = successReader;
            ErrorReader = errorReader;
            OwnsResponse = ownsResponse;
        }

        public string Status { get; }

        public bool IsSuccess { get; }

        public ApiBodyReader<T>? SuccessReader { get; }

        public ApiErrorReader? ErrorReader { get; }

        public bool OwnsResponse { get; }
    }
}
