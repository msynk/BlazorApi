using System.Globalization;
using System.Text;
using BlazorApi.OpenApi.Diagnostics;

namespace BlazorApi.OpenApi.Syntax;

/// <summary>The kind of a <see cref="DocNode"/>, following the JSON data model.</summary>
public enum NodeKind
{
    /// <summary>A mapping (JSON object).</summary>
    Object,

    /// <summary>A sequence (JSON array).</summary>
    Array,

    /// <summary>A string scalar.</summary>
    String,

    /// <summary>A number scalar.</summary>
    Number,

    /// <summary>A boolean scalar.</summary>
    Boolean,

    /// <summary>A null scalar.</summary>
    Null,
}

/// <summary>
/// A node of a parsed JSON or YAML document. The tree keeps source positions for diagnostics and preserves the
/// order of mapping keys, so everything derived from it is deterministic.
/// </summary>
public abstract class DocNode
{
    private protected DocNode(SourceLocation location) => Location = location;

    /// <summary>Where the node starts in its source document.</summary>
    public SourceLocation Location { get; }

    /// <summary>The node kind.</summary>
    public abstract NodeKind Kind { get; }

    /// <summary>
    /// The number of nodes this node expands to, counting shared (YAML alias) subtrees every time they occur. Used to
    /// bound the work done on untrusted documents.
    /// </summary>
    internal long Weight { get; private protected set; } = 1;

    /// <summary>A short, lower-case description of the kind ("an object", "a string") for messages.</summary>
    public string KindDescription => Kind switch
    {
        NodeKind.Object => "an object",
        NodeKind.Array => "an array",
        NodeKind.String => "a string",
        NodeKind.Number => "a number",
        NodeKind.Boolean => "a boolean",
        _ => "null",
    };

    /// <summary>Serializes the node as compact JSON. Mapping keys keep their document order.</summary>
    public string ToJson()
    {
        var builder = new StringBuilder();
        WriteJson(builder);
        return builder.ToString();
    }

    /// <summary>Appends the compact JSON form of the node.</summary>
    public abstract void WriteJson(StringBuilder builder);

    /// <summary>
    /// Compares two nodes structurally: same kind, equal scalar values (numbers compared by value), equal array
    /// items in order, and equal object entries regardless of key order.
    /// </summary>
    public static bool DeepEquals(DocNode? left, DocNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Kind != right.Kind)
        {
            return false;
        }

        switch (left)
        {
            case ScalarNode ls:
                var rs = (ScalarNode)right;
                if (ls.Kind == NodeKind.Number && ls.TryGetDecimal(out var ld) && rs.TryGetDecimal(out var rd))
                {
                    return ld == rd;
                }

                return string.Equals(ls.Value, rs.Value, StringComparison.Ordinal);
            case ArrayNode la:
                var ra = (ArrayNode)right;
                if (la.Count != ra.Count)
                {
                    return false;
                }

                for (var i = 0; i < la.Count; i++)
                {
                    if (!DeepEquals(la[i], ra[i]))
                    {
                        return false;
                    }
                }

                return true;
            case ObjectNode lo:
                var ro = (ObjectNode)right;
                if (lo.Count != ro.Count)
                {
                    return false;
                }

                foreach (var property in lo.Properties)
                {
                    if (!ro.TryGetValue(property.Name, out var other) || !DeepEquals(property.Value, other))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    internal static void WriteJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}

/// <summary>A mapping entry.</summary>
public sealed class PropertyNode
{
    internal PropertyNode(string name, SourceLocation keyLocation, DocNode value)
    {
        Name = name;
        KeyLocation = keyLocation;
        Value = value;
    }

    /// <summary>The key.</summary>
    public string Name { get; }

    /// <summary>Where the key starts.</summary>
    public SourceLocation KeyLocation { get; }

    /// <summary>The value.</summary>
    public DocNode Value { get; internal set; }
}

/// <summary>A mapping (JSON object) with ordered, unique keys.</summary>
public sealed class ObjectNode : DocNode
{
    private const int IndexThreshold = 8;
    private readonly List<PropertyNode> _properties = [];
    private Dictionary<string, int>? _index;

    internal ObjectNode(SourceLocation location)
        : base(location)
    {
    }

    /// <inheritdoc />
    public override NodeKind Kind => NodeKind.Object;

    /// <summary>The entries in document order.</summary>
    public IReadOnlyList<PropertyNode> Properties => _properties;

    /// <summary>The number of entries.</summary>
    public int Count => _properties.Count;

    /// <summary>Gets the value of a key, or <see langword="null"/> when absent.</summary>
    public DocNode? this[string name] => TryGetValue(name, out var value) ? value : null;

    /// <summary>Tries to get the value of a key.</summary>
    public bool TryGetValue(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DocNode? value)
    {
        var index = IndexOf(name);
        value = index >= 0 ? _properties[index].Value : null;
        return value is not null;
    }

    /// <summary>Gets the entry for a key, or <see langword="null"/> when absent.</summary>
    public PropertyNode? GetProperty(string name)
    {
        var index = IndexOf(name);
        return index >= 0 ? _properties[index] : null;
    }

    /// <summary>Whether the key is present.</summary>
    public bool ContainsKey(string name) => IndexOf(name) >= 0;

    /// <summary>Adds an entry. Returns <see langword="false"/> (and replaces the value) when the key already exists.</summary>
    internal bool Add(string name, SourceLocation keyLocation, DocNode value)
    {
        var existing = IndexOf(name);
        if (existing >= 0)
        {
            Weight += value.Weight - _properties[existing].Value.Weight;
            _properties[existing].Value = value;
            return false;
        }

        _properties.Add(new PropertyNode(name, keyLocation, value));
        Weight += value.Weight;
        if (_index is not null)
        {
            _index[name] = _properties.Count - 1;
        }
        else if (_properties.Count > IndexThreshold)
        {
            _index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < _properties.Count; i++)
            {
                _index[_properties[i].Name] = i;
            }
        }

        return true;
    }

    private int IndexOf(string name)
    {
        if (_index is not null)
        {
            return _index.TryGetValue(name, out var i) ? i : -1;
        }

        for (var i = 0; i < _properties.Count; i++)
        {
            if (string.Equals(_properties[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public override void WriteJson(StringBuilder builder)
    {
        builder.Append('{');
        for (var i = 0; i < _properties.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            WriteJsonString(builder, _properties[i].Name);
            builder.Append(':');
            _properties[i].Value.WriteJson(builder);
        }

        builder.Append('}');
    }
}

/// <summary>A sequence (JSON array).</summary>
public sealed class ArrayNode : DocNode
{
    private readonly List<DocNode> _items = [];

    internal ArrayNode(SourceLocation location)
        : base(location)
    {
    }

    /// <inheritdoc />
    public override NodeKind Kind => NodeKind.Array;

    /// <summary>The items in order.</summary>
    public IReadOnlyList<DocNode> Items => _items;

    /// <summary>The number of items.</summary>
    public int Count => _items.Count;

    /// <summary>Gets an item.</summary>
    public DocNode this[int index] => _items[index];

    internal void Add(DocNode item)
    {
        _items.Add(item);
        Weight += item.Weight;
    }

    /// <inheritdoc />
    public override void WriteJson(StringBuilder builder)
    {
        builder.Append('[');
        for (var i = 0; i < _items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            _items[i].WriteJson(builder);
        }

        builder.Append(']');
    }
}

/// <summary>A scalar: string, number, boolean or null.</summary>
public sealed class ScalarNode : DocNode
{
    private readonly NodeKind _kind;

    internal ScalarNode(SourceLocation location, NodeKind kind, string value, string? text = null)
        : base(location)
    {
        _kind = kind;
        Value = value;
        Text = text ?? value;
    }

    /// <inheritdoc />
    public override NodeKind Kind => _kind;

    /// <summary>
    /// The scalar value. Numbers are kept in JSON number syntax as written (so large integers and decimals lose no
    /// precision; YAML-only forms such as <c>+1</c> or <c>0x1F</c> are normalized); booleans are
    /// <c>true</c>/<c>false</c>; null is <c>null</c>.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The scalar exactly as written in the source. It differs from <see cref="Value"/> only for YAML plain scalars
    /// that resolve to a non-string (<c>version: 1.10</c> has the number value <c>1.10</c> and the text
    /// <c>1.10</c>; <c>empty:</c> has the null value and the text <c>""</c>). Use it where a string is expected.
    /// </summary>
    public string Text { get; }

    /// <summary>Whether this is a string scalar.</summary>
    public bool IsString => _kind == NodeKind.String;

    /// <summary>Whether this is the null scalar.</summary>
    public bool IsNull => _kind == NodeKind.Null;

    /// <summary>Gets the boolean value when this is a boolean scalar.</summary>
    public bool TryGetBoolean(out bool value)
    {
        value = _kind == NodeKind.Boolean && Value == "true";
        return _kind == NodeKind.Boolean;
    }

    /// <summary>Gets the value as a 64-bit integer when this is an integral number in range.</summary>
    public bool TryGetInt64(out long value)
    {
        value = 0;
        if (_kind != NodeKind.Number)
        {
            return false;
        }

        if (long.TryParse(Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        // 1.0 and 1e2 are integral too.
        if (TryGetDecimal(out var d) && d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue)
        {
            value = (long)d;
            return true;
        }

        return false;
    }

    /// <summary>Gets the value as a decimal when this is a number within decimal range.</summary>
    public bool TryGetDecimal(out decimal value)
    {
        value = 0;
        return _kind == NodeKind.Number &&
            decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Gets the value as a double when this is a number.</summary>
    public bool TryGetDouble(out double value)
    {
        value = 0;
        return _kind == NodeKind.Number &&
            double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <inheritdoc />
    public override void WriteJson(StringBuilder builder)
    {
        if (_kind == NodeKind.String)
        {
            WriteJsonString(builder, Value);
        }
        else
        {
            builder.Append(Value);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
