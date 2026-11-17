using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BlazorApi.Runtime.Http;

/// <summary>
/// Logs API operations with their semantics instead of raw URLs: the operation id, method, path template, status code
/// and duration of every request a generated client sends (each retry attempt when added after the retry handler).
/// It warns once per operation when a response announces that the operation is deprecated (<c>Deprecation</c>,
/// RFC 9745) or will be removed (<c>Sunset</c>, RFC 8594). URLs, query strings, headers and bodies are never logged.
/// Add it with <c>AddApiLogging()</c> on the client's <c>IHttpClientBuilder</c>.
/// </summary>
public sealed class ApiLoggingHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<string, bool> Warned = new(StringComparer.Ordinal);

    private static readonly Action<ILogger, string, string, string, int, double, Exception?> Completed =
        LoggerMessage.Define<string, string, string, int, double>(LogLevel.Debug, new EventId(1, "ApiOperationCompleted"),
            "{OperationId} ({Method} {UrlTemplate}) responded {StatusCode} in {ElapsedMilliseconds:0.#} ms");

    private static readonly Action<ILogger, string, string, string, int, double, Exception?> CompletedWithError =
        LoggerMessage.Define<string, string, string, int, double>(LogLevel.Information, new EventId(2, "ApiOperationErrorStatus"),
            "{OperationId} ({Method} {UrlTemplate}) responded {StatusCode} in {ElapsedMilliseconds:0.#} ms");

    private static readonly Action<ILogger, string, string, string, double, Exception?> Failed =
        LoggerMessage.Define<string, string, string, double>(LogLevel.Warning, new EventId(3, "ApiOperationFailed"),
            "{OperationId} ({Method} {UrlTemplate}) failed after {ElapsedMilliseconds:0.#} ms");

    private static readonly Action<ILogger, string, string, string, string, Exception?> Deprecated =
        LoggerMessage.Define<string, string, string, string>(LogLevel.Warning, new EventId(4, "ApiOperationDeprecated"),
            "{OperationId} ({Method} {UrlTemplate}) is deprecated by the server: {Details}");

    private readonly ILogger _logger;

    /// <summary>Creates the handler.</summary>
    public ApiLoggingHandler(ILogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var operationId = request.Options.TryGetValue(ApiRequestOptionKeys.OperationId, out var id) ? id : "(request)";
        var template = request.Options.TryGetValue(ApiRequestOptionKeys.PathTemplate, out var path) ? path : "?";
        var method = request.Method.Method;
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Failed(_logger, operationId, method, template, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex);
            throw;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var status = (int)response.StatusCode;
        (status >= 400 ? CompletedWithError : Completed)(_logger, operationId, method, template, status, elapsed, null);

        if (DeprecationDetails(response) is { } details && Warned.TryAdd(method + " " + template + " " + operationId, true))
        {
            Deprecated(_logger, operationId, method, template, details, null);
        }

        return response;
    }

    /// <summary>The <c>Deprecation</c>, <c>Sunset</c> and deprecation <c>Link</c> information of a response.</summary>
    internal static string? DeprecationDetails(HttpResponseMessage response)
    {
        var parts = new List<string>();
        if (response.Headers.TryGetValues("Deprecation", out var deprecation))
        {
            parts.Add("Deprecation: " + string.Join(", ", deprecation));
        }

        if (response.Headers.TryGetValues("Sunset", out var sunset))
        {
            parts.Add("Sunset: " + string.Join(", ", sunset));
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var headers = ApiHeaders.From(response);
        foreach (var relation in new[] { "deprecation", "sunset" })
        {
            if (ApiLinkHeader.GetLink(headers, relation) is { } link)
            {
                parts.Add("see " + link);
            }
        }

        return string.Join("; ", parts);
    }
}
