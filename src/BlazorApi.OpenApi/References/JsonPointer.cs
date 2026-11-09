using System.Globalization;
using System.Text;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.References;

/// <summary>RFC 6901 JSON Pointer evaluation over <see cref="DocNode"/> trees.</summary>
public static class JsonPointer
{
    /// <summary>
    /// Evaluates a pointer given as a URI fragment (with or without the leading <c>#</c>; percent-encoding is decoded).
    /// An empty pointer selects the root.
    /// </summary>
    /// <returns>The selected node, or <see langword="null"/> when a segment does not exist.</returns>
    public static DocNode? Evaluate(DocNode root, string fragment, out string? error)
    {
        error = null;
        var pointer = fragment.StartsWith("#", StringComparison.Ordinal) ? fragment.Substring(1) : fragment;
        pointer = PercentDecode(pointer);
        if (pointer.Length == 0)
        {
            return root;
        }

        if (pointer[0] != '/')
        {
            error = "the fragment is not a JSON pointer (anchor references are not supported)";
            return null;
        }

        var current = root;
        var segments = pointer.Substring(1).Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = Unescape(segments[i]);
            switch (current)
            {
                case ObjectNode obj:
                    if (!obj.TryGetValue(segment, out var next))
                    {
                        error = $"'{segment}' does not exist at /{string.Join("/", segments, 0, i)}";
                        return null;
                    }

                    current = next;
                    break;
                case ArrayNode array:
                    if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= array.Count)
                    {
                        error = $"'{segment}' is not a valid index at /{string.Join("/", segments, 0, i)}";
                        return null;
                    }

                    current = array[index];
                    break;
                default:
                    error = $"/{string.Join("/", segments, 0, i)} is {current.KindDescription}, not a container";
                    return null;
            }
        }

        return current;
    }

    /// <summary>Escapes a key for use as a pointer segment (<c>~</c> to <c>~0</c>, <c>/</c> to <c>~1</c>).</summary>
    public static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    /// <summary>Unescapes a pointer segment.</summary>
    public static string Unescape(string segment) => segment.Replace("~1", "/").Replace("~0", "~");

    private static string PercentDecode(string value)
    {
        if (value.IndexOf('%') < 0)
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%' && i + 2 < value.Length && JsonParser.HexValue(value[i + 1]) >= 0 && JsonParser.HexValue(value[i + 2]) >= 0)
            {
                bytes.Add((byte)((JsonParser.HexValue(value[i + 1]) * 16) + JsonParser.HexValue(value[i + 2])));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
