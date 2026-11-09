using System.Collections;
using System.Collections.Immutable;

namespace BlazorApi.SourceGenerator;

/// <summary>An immutable array with value equality, so incremental pipeline steps cache correctly.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(ImmutableArray<T> items) => _items = items;

    public int Count => _items.IsDefault ? 0 : _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(EquatableArray<T> other)
    {
        if (Count != other.Count)
        {
            return false;
        }

        for (var i = 0; i < Count; i++)
        {
            if (!_items[i].Equals(other._items[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            for (var i = 0; i < Count; i++)
            {
                hash = (hash * 31) + _items[i].GetHashCode();
            }

            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() => (_items.IsDefault ? ImmutableArray<T>.Empty : _items).AsEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
