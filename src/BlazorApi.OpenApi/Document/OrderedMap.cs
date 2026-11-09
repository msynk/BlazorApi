using System.Collections;

namespace BlazorApi.OpenApi.Document;

/// <summary>
/// A read-only string-keyed dictionary that enumerates in insertion (document) order, so everything derived from an
/// API description is deterministic.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class OrderedMap<TValue> : IReadOnlyDictionary<string, TValue>
{
    private readonly List<KeyValuePair<string, TValue>> _entries = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <summary>An empty map.</summary>
    public static OrderedMap<TValue> Empty { get; } = new();

    /// <inheritdoc />
    public int Count => _entries.Count;

    /// <inheritdoc />
    public IEnumerable<string> Keys => _entries.Select(e => e.Key);

    /// <inheritdoc />
    public IEnumerable<TValue> Values => _entries.Select(e => e.Value);

    /// <summary>The entries in order.</summary>
    public IReadOnlyList<KeyValuePair<string, TValue>> Entries => _entries;

    /// <inheritdoc />
    public TValue this[string key] => _entries[_index[key]].Value;

    /// <inheritdoc />
    public bool ContainsKey(string key) => _index.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, out TValue value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            value = _entries[i].Value;
            return true;
        }

        value = default!;
        return false;
    }

    internal void Set(string key, TValue value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            _entries[i] = new KeyValuePair<string, TValue>(key, value);
            return;
        }

        _index[key] = _entries.Count;
        _entries.Add(new KeyValuePair<string, TValue>(key, value));
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
