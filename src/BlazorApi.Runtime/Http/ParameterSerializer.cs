using System.Text;

namespace BlazorApi.Runtime.Http;

/// <summary>The kind of a <see cref="ParameterValue"/>.</summary>
public enum ParameterValueKind
{
    /// <summary>No value: the parameter is omitted.</summary>
    None,

    /// <summary>A primitive value.</summary>
    Scalar,

    /// <summary>An array of primitive values.</summary>
    Array,

    /// <summary>An object: ordered name/value pairs.</summary>
    Object,
}

/// <summary>A parameter value already formatted to wire strings, ready for style-based serialization.</summary>
public readonly struct ParameterValue
{
    private ParameterValue(ParameterValueKind kind, string? scalar, IReadOnlyList<string>? items, IReadOnlyList<KeyValuePair<string, string>>? properties)
    {
        Kind = kind;
        ScalarValue = scalar;
        Items = items;
        Properties = properties;
    }

    /// <summary>The value kind.</summary>
    public ParameterValueKind Kind { get; }

    /// <summary>The scalar value.</summary>
    public string? ScalarValue { get; }

    /// <summary>The array items.</summary>
    public IReadOnlyList<string>? Items { get; }

    /// <summary>The object properties.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Properties { get; }

    /// <summary>No value.</summary>
    public static ParameterValue None => default;

    /// <summary>A scalar; <see langword="null"/> means no value.</summary>
    public static ParameterValue Scalar(string? value) =>
        value is null ? default : new ParameterValue(ParameterValueKind.Scalar, value, null, null);

    /// <summary>An array; <see langword="null"/> means no value.</summary>
    public static ParameterValue Array(IEnumerable<string>? items) =>
        items is null ? default : new ParameterValue(ParameterValueKind.Array, null, items.ToArray(), null);

    /// <summary>An object; <see langword="null"/> means no value. Pairs with a null value are skipped.</summary>
    public static ParameterValue Object(IEnumerable<KeyValuePair<string, string?>>? properties) =>
        properties is null
            ? default
            : new ParameterValue(ParameterValueKind.Object, null, null,
                properties.Where(p => p.Value is not null).Select(p => new KeyValuePair<string, string>(p.Key, p.Value!)).ToArray());
}

/// <summary>Parameter serialization styles (OpenAPI <c>style</c>).</summary>
public enum ParameterStyle
{
    /// <summary><c>simple</c>: comma-separated (path, header).</summary>
    Simple,

    /// <summary><c>form</c>: <c>name=value</c> (query, cookie).</summary>
    Form,

    /// <summary><c>matrix</c>: <c>;name=value</c> (path).</summary>
    Matrix,

    /// <summary><c>label</c>: <c>.value</c> (path).</summary>
    Label,

    /// <summary><c>spaceDelimited</c> arrays (query).</summary>
    SpaceDelimited,

    /// <summary><c>pipeDelimited</c> arrays (query).</summary>
    PipeDelimited,

    /// <summary><c>deepObject</c>: <c>name[key]=value</c> (query).</summary>
    DeepObject,
}

/// <summary>Serializes parameters following the OpenAPI style and explode rules (RFC 6570 based).</summary>
public static class ParameterSerializer
{
    /// <summary>Serializes a path parameter to its encoded path text.</summary>
    public static string Path(string name, ParameterValue value, ParameterStyle style, bool explode)
    {
        var e = (Func<string, string>)Uri.EscapeDataString;
        switch (value.Kind)
        {
            case ParameterValueKind.None:
                throw new ArgumentException($"The path parameter '{name}' requires a value.", nameof(value));
            case ParameterValueKind.Scalar:
                var scalar = e(value.ScalarValue!);
                return style switch
                {
                    ParameterStyle.Label => "." + scalar,
                    ParameterStyle.Matrix => ";" + e(name) + "=" + scalar,
                    _ => scalar,
                };
            case ParameterValueKind.Array:
                var items = value.Items!.Select(e).ToArray();
                return style switch
                {
                    ParameterStyle.Label => "." + string.Join(explode ? "." : ",", items),
                    ParameterStyle.Matrix => explode
                        ? string.Concat(items.Select(i => ";" + e(name) + "=" + i))
                        : ";" + e(name) + "=" + string.Join(",", items),
                    _ => string.Join(",", items),
                };
            default:
                var pairs = value.Properties!.Select(p => (Key: e(p.Key), Value: e(p.Value))).ToArray();
                return style switch
                {
                    ParameterStyle.Label => "." + (explode
                        ? string.Join(".", pairs.Select(p => p.Key + "=" + p.Value))
                        : string.Join(",", pairs.Select(p => p.Key + "," + p.Value))),
                    ParameterStyle.Matrix => explode
                        ? string.Concat(pairs.Select(p => ";" + p.Key + "=" + p.Value))
                        : ";" + e(name) + "=" + string.Join(",", pairs.Select(p => p.Key + "," + p.Value)),
                    _ => explode
                        ? string.Join(",", pairs.Select(p => p.Key + "=" + p.Value))
                        : string.Join(",", pairs.Select(p => p.Key + "," + p.Value)),
                };
        }
    }

    /// <summary>Appends a query parameter's encoded <c>name=value</c> pairs to <paramref name="query"/>.</summary>
    public static void Query(ICollection<string> query, string name, ParameterValue value, ParameterStyle style, bool explode, bool allowReserved = false)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var encode = allowReserved ? (Func<string, string>)EncodeAllowReserved : Uri.EscapeDataString;
        var n = Uri.EscapeDataString(name);
        switch (value.Kind)
        {
            case ParameterValueKind.None:
                return;
            case ParameterValueKind.Scalar:
                query.Add(n + "=" + encode(value.ScalarValue!));
                return;
            case ParameterValueKind.Array:
                var items = value.Items!;
                if (items.Count == 0)
                {
                    return;
                }

                switch (style)
                {
                    case ParameterStyle.SpaceDelimited:
                        query.Add(n + "=" + string.Join("%20", items.Select(encode)));
                        return;
                    case ParameterStyle.PipeDelimited:
                        query.Add(n + "=" + string.Join("%7C", items.Select(encode)));
                        return;
                    default:
                        if (explode)
                        {
                            foreach (var item in items)
                            {
                                query.Add(n + "=" + encode(item));
                            }
                        }
                        else
                        {
                            query.Add(n + "=" + string.Join(",", items.Select(encode)));
                        }

                        return;
                }

            default:
                var pairs = value.Properties!;
                if (style == ParameterStyle.DeepObject)
                {
                    foreach (var pair in pairs)
                    {
                        query.Add(n + "%5B" + Uri.EscapeDataString(pair.Key) + "%5D=" + encode(pair.Value));
                    }
                }
                else if (explode)
                {
                    foreach (var pair in pairs)
                    {
                        query.Add(Uri.EscapeDataString(pair.Key) + "=" + encode(pair.Value));
                    }
                }
                else if (pairs.Count > 0)
                {
                    query.Add(n + "=" + string.Join(",", pairs.Select(p => Uri.EscapeDataString(p.Key) + "," + encode(p.Value))));
                }

                return;
        }
    }

    /// <summary>Serializes a header parameter (style <c>simple</c>), or returns <see langword="null"/> when there is no value.</summary>
    public static string? Header(ParameterValue value, bool explode) => value.Kind switch
    {
        ParameterValueKind.None => null,
        ParameterValueKind.Scalar => value.ScalarValue,
        ParameterValueKind.Array => string.Join(",", value.Items!),
        _ => explode
            ? string.Join(",", value.Properties!.Select(p => p.Key + "=" + p.Value))
            : string.Join(",", value.Properties!.Select(p => p.Key + "," + p.Value)),
    };

    /// <summary>Serializes a cookie parameter (style <c>form</c>) to <c>name=value</c> pairs.</summary>
    public static IEnumerable<string> Cookie(string name, ParameterValue value, bool explode)
    {
        switch (value.Kind)
        {
            case ParameterValueKind.None:
                yield break;
            case ParameterValueKind.Scalar:
                yield return name + "=" + Uri.EscapeDataString(value.ScalarValue!);
                break;
            case ParameterValueKind.Array:
                if (explode)
                {
                    foreach (var item in value.Items!)
                    {
                        yield return name + "=" + Uri.EscapeDataString(item);
                    }
                }
                else
                {
                    yield return name + "=" + string.Join(",", value.Items!.Select(Uri.EscapeDataString));
                }

                break;
            default:
                if (explode)
                {
                    foreach (var pair in value.Properties!)
                    {
                        yield return pair.Key + "=" + Uri.EscapeDataString(pair.Value);
                    }
                }
                else
                {
                    yield return name + "=" + string.Join(",", value.Properties!.Select(p => Uri.EscapeDataString(p.Key) + "," + Uri.EscapeDataString(p.Value)));
                }

                break;
        }
    }

    /// <summary>Percent-encodes everything except unreserved and reserved characters (<c>allowReserved: true</c>).</summary>
    private static string EncodeAllowReserved(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            var keep = b < 0x80 && (char.IsLetterOrDigit(c) || "-._~:/?#[]@!$'()*+,;=".IndexOf(c) >= 0);
            if (keep)
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
