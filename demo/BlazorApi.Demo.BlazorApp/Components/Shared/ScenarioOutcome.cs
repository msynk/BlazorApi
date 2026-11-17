using BlazorApi.Runtime;

namespace BlazorApi.Demo.BlazorApp.Components.Shared;

/// <summary>What a playground scenario observed.</summary>
public sealed record ScenarioOutcome(bool Success, string Summary, IReadOnlyList<string> Details, ApiError? Error = null, Exception? Exception = null)
{
    public static ScenarioOutcome Ok(string summary, params string[] details) => new(true, summary, details);

    public static ScenarioOutcome Fail(string summary, params string[] details) => new(false, summary, details);

    /// <summary>An outcome describing a failed result: its kind, status and the error the library describes.</summary>
    public static ScenarioOutcome From(ApiError error, params string[] details) => new(
        false,
        error.StatusCode is { } status ? $"ApiErrorKind.{error.Kind} · HTTP {status}" : $"ApiErrorKind.{error.Kind}",
        details,
        error);
}
