using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorApi.Runtime.Json;

/// <summary>
/// ISO 8601 durations (<c>format: duration</c>, such as <c>PT1H30M</c> or <c>P2DT0.5S</c>) as <see cref="TimeSpan"/>.
/// Weeks, days, hours, minutes and (fractional) seconds are supported; years and months have no fixed length and are
/// rejected unless zero. A leading <c>-</c> denotes a negative duration (XML Schema convention).
/// </summary>
public static class ApiDuration
{
    /// <summary>Formats a duration (<c>PT1H30M</c>; zero is <c>PT0S</c>).</summary>
    public static string Format(TimeSpan value)
    {
        var builder = new StringBuilder();
        var ticks = value.Ticks;
        if (ticks < 0)
        {
            builder.Append('-');
        }

        // Work with the magnitude as a decimal so TimeSpan.MinValue does not overflow.
        var magnitude = Math.Abs((decimal)ticks);
        var days = decimal.Truncate(magnitude / TimeSpan.TicksPerDay);
        var rest = magnitude - (days * TimeSpan.TicksPerDay);
        builder.Append('P');
        if (days > 0)
        {
            builder.Append(days.ToString(CultureInfo.InvariantCulture)).Append('D');
        }

        if (rest > 0 || days == 0)
        {
            builder.Append('T');
            var hours = decimal.Truncate(rest / TimeSpan.TicksPerHour);
            rest -= hours * TimeSpan.TicksPerHour;
            var minutes = decimal.Truncate(rest / TimeSpan.TicksPerMinute);
            rest -= minutes * TimeSpan.TicksPerMinute;
            if (hours > 0)
            {
                builder.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H');
            }

            if (minutes > 0)
            {
                builder.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
            }

            if (rest > 0 || (hours == 0 && minutes == 0))
            {
                builder.Append((rest / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture)).Append('S');
            }
        }

        return builder.ToString();
    }

    /// <summary>Parses a duration.</summary>
    /// <exception cref="FormatException">The text is not a supported ISO 8601 duration.</exception>
    public static TimeSpan Parse(string text) =>
        TryParse(text, out var value, out var error) ? value : throw new FormatException(error);

    /// <summary>Parses a duration, or returns false.</summary>
    public static bool TryParse(string? text, out TimeSpan value) => TryParse(text, out value, out _);

    private static bool TryParse(string? text, out TimeSpan value, out string error)
    {
        value = default;
        error = $"'{text}' is not an ISO 8601 duration (such as PT1H30M)";
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var i = 0;
        var negative = text![0] == '-';
        if (negative || text[0] == '+')
        {
            i++;
        }

        if (i >= text.Length || text[i] != 'P')
        {
            return false;
        }

        i++;
        decimal ticks = 0;
        var inTime = false;
        var any = false;
        var lastUnit = -1;
        while (i < text.Length)
        {
            if (text[i] == 'T')
            {
                if (inTime || i == text.Length - 1)
                {
                    return false;
                }

                inTime = true;
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or ','))
            {
                i++;
            }

            if (i == start || i >= text.Length ||
                !decimal.TryParse(text.Substring(start, i - start).Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            var designator = text[i++];
            var (unit, unitTicks) = (inTime, designator) switch
            {
                (false, 'Y') => (0, -1m),
                (false, 'M') => (1, -1m),
                (false, 'W') => (2, TimeSpan.TicksPerDay * 7m),
                (false, 'D') => (3, (decimal)TimeSpan.TicksPerDay),
                (true, 'H') => (4, (decimal)TimeSpan.TicksPerHour),
                (true, 'M') => (5, (decimal)TimeSpan.TicksPerMinute),
                (true, 'S') => (6, (decimal)TimeSpan.TicksPerSecond),
                _ => (-1, 0m),
            };
            if (unit <= lastUnit)
            {
                return false;
            }

            if (unitTicks < 0)
            {
                if (number != 0)
                {
                    error = $"'{text}' uses years or months, which have no fixed length as a TimeSpan";
                    return false;
                }

                unitTicks = 0;
            }

            lastUnit = unit;
            any = true;
            if (unitTicks > 0 && number > (TimeSpan.MaxValue.Ticks - ticks) / unitTicks)
            {
                return false; // Longer than TimeSpan.MaxValue (decimal multiplication would overflow).
            }

            ticks += number * unitTicks;
        }

        if (!any || ticks > TimeSpan.MaxValue.Ticks)
        {
            return false;
        }

        value = TimeSpan.FromTicks((long)decimal.Round(ticks));
        if (negative)
        {
            value = value.Negate();
        }

        return true;
    }
}

/// <summary>Reads and writes <see cref="TimeSpan"/> values as ISO 8601 durations (see <see cref="ApiDuration"/>).</summary>
public sealed class ApiDurationJsonConverter : JsonConverter<TimeSpan>
{
    /// <inheritdoc />
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A duration must be a JSON string.");
        }

        var text = reader.GetString();
        return ApiDuration.TryParse(text, out var value)
            ? value
            : throw new JsonException($"'{text}' is not an ISO 8601 duration (such as PT1H30M) without years or months.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        (writer ?? throw new ArgumentNullException(nameof(writer))).WriteStringValue(ApiDuration.Format(value));
}
