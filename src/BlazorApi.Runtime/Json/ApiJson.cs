using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BlazorApi.Runtime.Json;

/// <summary>JSON helpers used by generated code.</summary>
public static class ApiJson
{
    /// <summary>
    /// Creates the default serializer options for generated clients: web defaults (case-insensitive reads),
    /// strict number handling, no global naming policy (generated types name every property explicitly), and the
    /// reflection-based metadata resolver when reflection is enabled. Trimmed and native AOT applications add their
    /// <see cref="JsonSerializerContext"/> through <see cref="JsonSerializerOptions.TypeInfoResolver"/>.
    /// </summary>
    public static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = DefaultResolver(),
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        NumberHandling = JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The reflection resolver is only created when reflection-based serialization is enabled, which trimmed and native AOT applications turn off.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflection resolver is only created when reflection-based serialization is enabled, which trimmed and native AOT applications turn off.")]
    private static IJsonTypeInfoResolver? DefaultResolver() =>
        JsonSerializer.IsReflectionEnabledByDefault ? new DefaultJsonTypeInfoResolver() : null;

    /// <summary>
    /// Gets the type metadata for <typeparamref name="T"/> from the options' resolver. With the default options this
    /// uses reflection; supply a <see cref="JsonSerializerContext"/> through
    /// <see cref="JsonSerializerOptions.TypeInfoResolver"/> for trimmed and native AOT applications.
    /// </summary>
    public static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
    }

    /// <summary>Serializes a value to a JSON string (used for <c>content</c>-described parameters).</summary>
    public static string Serialize<T>(T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(value, TypeInfo<T>(options));

    /// <summary>Deserializes a value from a JSON element (used by unions to read a variant on demand).</summary>
    public static T? Deserialize<T>(JsonElement element, JsonSerializerOptions options) =>
        element.Deserialize(TypeInfo<T>(options));

    /// <summary>Deserializes the current value of a reader as <typeparamref name="T"/>.</summary>
    public static T? Read<T>(ref Utf8JsonReader reader, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(ref reader, TypeInfo<T>(options));

    /// <summary>Serializes a value to a writer as <typeparamref name="T"/>.</summary>
    public static void Write<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, TypeInfo<T>(options));

    /// <summary>
    /// Reads the value of a discriminator property from the object at the reader's position without moving the
    /// reader (the reader is copied). Returns <see langword="null"/> when the property is absent or not a string or
    /// number.
    /// </summary>
    public static string? PeekDiscriminator(Utf8JsonReader reader, string propertyName)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        var depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != depth + 1)
            {
                continue;
            }

            var isMatch = reader.ValueTextEquals(propertyName);
            if (!reader.Read())
            {
                return null;
            }

            if (isMatch)
            {
                return reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray()),
                    JsonTokenType.True => "true",
                    JsonTokenType.False => "false",
                    _ => null,
                };
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return null;
    }
}
