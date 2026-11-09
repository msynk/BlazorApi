using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorApi.Runtime.Json;

/// <summary>
/// A value that may be absent, distinguishing "not sent" from "sent as null". Generated models use it for optional
/// nullable properties where the difference matters (for example JSON merge-patch bodies): an unset property is
/// omitted from the JSON, a property set to <see langword="null"/> is written as <c>null</c>.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T _value;

    /// <summary>Creates a present value.</summary>
    public Optional(T value)
    {
        _value = value;
        HasValue = true;
    }

    /// <summary>An absent value.</summary>
    public static Optional<T> Absent => default;

    /// <summary>Whether a value is present (it may still be <see langword="null"/>).</summary>
    public bool HasValue { get; }

    /// <summary>The value; throws when absent.</summary>
    public T Value => HasValue ? _value : throw new InvalidOperationException("The optional value is absent.");

    /// <summary>The value, or <paramref name="fallback"/> when absent.</summary>
    public T GetValueOrDefault(T fallback) => HasValue ? _value : fallback;

    /// <summary>Creates a present value.</summary>
    public static implicit operator Optional<T>(T value) => new(value);

    /// <inheritdoc />
    public bool Equals(Optional<T> other) =>
        HasValue == other.HasValue && (!HasValue || EqualityComparer<T>.Default.Equals(_value, other._value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HasValue ? HashCode.Combine(true, _value) : 0;

    /// <inheritdoc />
    public override string ToString() => HasValue ? _value?.ToString() ?? "null" : "<absent>";

    /// <summary>Compares two optionals.</summary>
    public static bool operator ==(Optional<T> left, Optional<T> right) => left.Equals(right);

    /// <summary>Compares two optionals.</summary>
    public static bool operator !=(Optional<T> left, Optional<T> right) => !left.Equals(right);
}

/// <summary>
/// The JSON converter for <see cref="Optional{T}"/>. Combine it with
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]</c> so absent values are omitted; generated
/// code applies both attributes with a closed generic type, which keeps it trimming and AOT safe.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.TokenType == JsonTokenType.Null ? default! : ApiJson.Read<T>(ref reader, options)!);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        if (!value.HasValue || value.Value is null)
        {
            writer.WriteNullValue();
            return;
        }

        ApiJson.Write(writer, value.Value, options);
    }
}
