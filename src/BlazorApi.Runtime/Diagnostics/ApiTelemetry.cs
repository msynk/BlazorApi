using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using BlazorApi.Runtime.Http;

namespace BlazorApi.Runtime.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible instrumentation of generated clients: an <see cref="ActivitySource"/> with one span per
/// operation call (the HTTP client spans of the call, including retries, are its children) and a <see cref="Meter"/>
/// with the duration of operation calls. Both are named <see cref="Name"/>; nothing is recorded unless a listener
/// (for example <c>AddSource("BlazorApi")</c> and <c>AddMeter("BlazorApi")</c> in OpenTelemetry) subscribes.
/// </summary>
/// <remarks>
/// Spans are named after the operation id and carry the attributes <c>http.request.method</c>,
/// <c>url.template</c>, <c>blazorapi.operation.id</c>, <c>http.response.status_code</c> and, on failure,
/// <c>error.type</c> (the status code, or <c>network</c>, <c>timeout</c>, <c>deserialization</c>, or the exception
/// type). The histogram <c>blazorapi.client.operation.duration</c> (seconds) has the same attributes. URLs and
/// parameter values are never recorded, so credentials and personal data in them do not leak into telemetry.
/// </remarks>
public static class ApiTelemetry
{
    /// <summary>The name of the activity source and the meter.</summary>
    public const string Name = "BlazorApi";

    private static readonly string? Version = typeof(ApiTelemetry).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

    /// <summary>The activity source of operation spans.</summary>
    public static ActivitySource ActivitySource { get; } = new(Name, Version);

    /// <summary>The meter of operation metrics.</summary>
    public static Meter Meter { get; } = new(Name, Version);

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "blazorapi.client.operation.duration",
        unit: "s",
        description: "Duration of API operation calls, including retries and reading the response body.");

    internal static Scope Start(ApiRequestBuilder builder)
    {
        var activity = ActivitySource.StartActivity(builder.OperationId, ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("http.request.method", builder.Method.Method);
            activity.SetTag("url.template", builder.PathTemplate);
            activity.SetTag("blazorapi.operation.id", builder.OperationId);
        }

        return new Scope(builder, activity, Duration.Enabled ? Stopwatch.GetTimestamp() : 0);
    }

    internal readonly struct Scope
    {
        private readonly ApiRequestBuilder _builder;
        private readonly Activity? _activity;
        private readonly long _started;

        public Scope(ApiRequestBuilder builder, Activity? activity, long started)
        {
            _builder = builder;
            _activity = activity;
            _started = started;
        }

        public void Complete(ApiResult result)
        {
            var errorType = result.Error is { } error ? ErrorType(error) : null;
            Finish(result.StatusCode == 0 ? null : result.StatusCode, errorType, result.Error?.Message);
        }

        public void Fail(Exception exception) =>
            Finish(null, exception is OperationCanceledException ? "cancelled" : exception.GetType().FullName, exception.Message);

        private void Finish(int? statusCode, string? errorType, string? description)
        {
            if (_started != 0)
            {
                var tags = new TagList
                {
                    { "http.request.method", _builder.Method.Method },
                    { "url.template", _builder.PathTemplate },
                    { "blazorapi.operation.id", _builder.OperationId },
                };
                if (statusCode is { } code)
                {
                    tags.Add("http.response.status_code", code);
                }

                if (errorType is not null)
                {
                    tags.Add("error.type", errorType);
                }

                Duration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);
            }

            if (_activity is null)
            {
                return;
            }

            if (statusCode is { } status)
            {
                _activity.SetTag("http.response.status_code", status);
            }

            if (errorType is not null)
            {
                _activity.SetTag("error.type", errorType);
                _activity.SetStatus(ActivityStatusCode.Error, description);
            }

            _activity.Dispose();
        }

        private static string ErrorType(ApiError error) => error.Kind switch
        {
            ApiErrorKind.Http => error.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "http",
            ApiErrorKind.Network => "network",
            ApiErrorKind.Timeout => "timeout",
            _ => "deserialization",
        };
    }
}
