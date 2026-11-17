using BlazorApi.Runtime.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds operation logging to a generated client's <see cref="IHttpClientBuilder"/>.</summary>
public static class ApiLoggingBuilderExtensions
{
    /// <summary>
    /// Adds an <see cref="ApiLoggingHandler"/> logging to the category <c>BlazorApi</c>: each request's operation id,
    /// method, path template, status and duration (Debug; Information for error statuses; Warning for failures), and
    /// a Warning the first time a response marks an operation deprecated (<c>Deprecation</c> or <c>Sunset</c>).
    /// Add it after <c>AddApiRetries</c> to log every attempt.
    /// </summary>
    public static IHttpClientBuilder AddApiLogging(this IHttpClientBuilder builder, string category = "BlazorApi")
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddHttpMessageHandler(services =>
            new ApiLoggingHandler((services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger(category)));
    }
}
