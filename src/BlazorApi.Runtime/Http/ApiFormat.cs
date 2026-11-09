using System.Globalization;

namespace BlazorApi.Runtime.Http;

/// <summary>
/// Formats parameter values as their OpenAPI wire strings: culture-invariant numbers, lower-case booleans, RFC 3339
/// dates and times.
/// </summary>
public static class ApiFormat
{
    /// <summary>Formats a string (unchanged).</summary>
    public static string Value(string value) => value;

    /// <summary>Formats a boolean as <c>true</c> or <c>false</c>.</summary>
    public static string Value(bool value) => value ? "true" : "false";

    /// <summary>Formats an integer.</summary>
    public static string Value(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an integer.</summary>
    public static string Value(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an integer.</summary>
    public static string Value(short value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an integer.</summary>
    public static string Value(sbyte value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an integer.</summary>
    public static string Value(byte value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an integer.</summary>
    public static string Value(ushort value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an integer.</summary>
    public static string Value(uint value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an integer.</summary>
    public static string Value(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a number with the shortest round-trippable representation.</summary>
    public static string Value(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Formats a number with the shortest round-trippable representation.</summary>
    public static string Value(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Formats a decimal.</summary>
    public static string Value(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a UUID in the hyphenated form.</summary>
    public static string Value(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Formats a date as an RFC 3339 full-date (<c>2024-05-01</c>).</summary>
    public static string Value(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Formats a time as an RFC 3339 partial time (<c>13:45:00</c>).</summary>
    public static string Value(TimeOnly value) => value.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    /// <summary>Formats a date-time as RFC 3339 (<c>2024-05-01T13:45:00+02:00</c>).</summary>
    public static string Value(DateTimeOffset value) =>
        value.ToString(value.Offset == TimeSpan.Zero ? "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    /// <summary>Formats a date-time as RFC 3339; unspecified kinds are treated as UTC.</summary>
    public static string Value(DateTime value) =>
        Value(value.Kind == DateTimeKind.Local ? new DateTimeOffset(value) : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

    /// <summary>Formats a URI as written.</summary>
    public static string Value(Uri value) => value?.OriginalString ?? throw new ArgumentNullException(nameof(value));

    /// <summary>Formats bytes as base64.</summary>
    public static string Value(byte[] value) => Convert.ToBase64String(value ?? throw new ArgumentNullException(nameof(value)));
}
