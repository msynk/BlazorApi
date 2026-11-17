using System.Globalization;

namespace BlazorApi.Runtime.Http;

/// <summary>
/// Reads typed values from response headers (<see cref="ApiResult.Headers"/>, <see cref="ApiError.Headers"/>). Used by
/// the generated <c>{Operation}ResponseHeaders</c> classes. Absent or unreadable headers give <see langword="null"/>:
/// headers describe a response, so a malformed one never fails the call.
/// </summary>
public static class ApiHeaderValues
{
    /// <summary>The header's value; several header lines are joined with <c>", "</c>.</summary>
    public static string? String(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name)
    {
        if (headers is null)
        {
            throw new ArgumentNullException(nameof(headers));
        }

        return headers.TryGetValue(name, out var values) && values.Count > 0 ? string.Join(", ", values) : null;
    }

    /// <summary>The comma-separated items of a header (OpenAPI <c>simple</c> style arrays).</summary>
    public static IReadOnlyList<string>? List(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        String(headers, name) is { } text
            ? text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
            : null;

    /// <summary>A 64-bit integer.</summary>
    public static long? Int64(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        long.TryParse(String(headers, name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>A 32-bit integer.</summary>
    public static int? Int32(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        int.TryParse(String(headers, name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>A floating-point number.</summary>
    public static double? Double(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        double.TryParse(String(headers, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>A decimal number.</summary>
    public static decimal? Decimal(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        decimal.TryParse(String(headers, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>A boolean (<c>true</c> or <c>false</c>, case-insensitive).</summary>
    public static bool? Boolean(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        bool.TryParse(String(headers, name), out var value) ? value : null;

    /// <summary>A date-time: an HTTP-date (RFC 9110, <c>Sun, 06 Nov 1994 08:49:37 GMT</c>) or RFC 3339.</summary>
    public static DateTimeOffset? DateTimeOffset(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name)
    {
        var text = String(headers, name);
        if (text is null)
        {
            return null;
        }

        if (System.DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var httpDate))
        {
            return httpDate;
        }

        return System.DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    }

    /// <summary>A date (<c>2024-05-01</c>).</summary>
    public static DateOnly? DateOnly(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        System.DateOnly.TryParseExact(String(headers, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    /// <summary>A UUID.</summary>
    public static Guid? Guid(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        System.Guid.TryParse(String(headers, name), out var value) ? value : null;

    /// <summary>A URI (absolute or relative, such as a <c>Location</c>).</summary>
    public static Uri? Uri(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        System.Uri.TryCreate(String(headers, name), UriKind.RelativeOrAbsolute, out var value) ? value : null;

    /// <summary>An ISO 8601 duration.</summary>
    public static TimeSpan? Duration(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string name) =>
        Json.ApiDuration.TryParse(String(headers, name), out var value) ? value : null;
}
