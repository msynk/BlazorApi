using BlazorApi.Runtime;
using DotNetQuery;

namespace BlazorApi.DotNetQuery;

/// <summary>
/// Defaults that generated query definitions use: failed queries are retried only when the failure is transient
/// (network errors, timeouts, 408, 429 and 5xx responses) and never for client errors such as 400 or 404.
/// </summary>
public static class ApiQueryDefaults
{
    /// <summary>The default number of attempts after the first failure.</summary>
    public const int MaxRetries = 3;

    /// <summary>Query options with <see cref="ShouldRetry"/>. Pass your own options to a generated definition to replace them.</summary>
    public static QueryOptions Options { get; } = new() { ShouldRetry = ShouldRetry };

    /// <summary>Retries transient failures up to <see cref="MaxRetries"/> times.</summary>
    public static bool ShouldRetry(int failureCount, Exception error) => failureCount <= MaxRetries && IsTransient(error);

    /// <summary>Whether an exception from a generated client is worth retrying.</summary>
    public static bool IsTransient(Exception error) => error switch
    {
        ApiNetworkException or ApiTimeoutException => true,
        ApiDeserializationException => false,
        ApiException api => api.StatusCode is 408 or 429 or >= 500,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>Applies the defaults to options (keeps any retry predicate already set).</summary>
    public static QueryOptions WithDefaults(this QueryOptions? options) =>
        options is null ? Options : options.ShouldRetry is null ? options with { ShouldRetry = ShouldRetry } : options;
}
