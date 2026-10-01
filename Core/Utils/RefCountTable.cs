using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.Utils;

/// <summary>
/// Values by key, each with a reference count. <see cref="Add"/> puts a value in with one reference,
/// <see cref="TryAcquire"/> takes another, <see cref="Release"/> drops one and hands the value back when it was the last
/// (so the caller frees what it holds). A value is replaced whole with <see cref="Set"/>, never changed in place. Not
/// thread-safe. The render state keeps its GPU resources (materials, textures, models, draw groups) in these.
/// </summary>
public sealed class RefCountTable<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, (TValue Value, int Refs)> _entries = [];

    public int Count => _entries.Count;

    /// <summary>Every key with its value, for the rare pass over all of them (an asset changed).</summary>
    public IEnumerable<(TKey Key, TValue Value)> Entries => _entries.Select(e => (e.Key, e.Value.Value));

    /// <summary>Takes another reference to an existing value; false when there is none (then <see cref="Add"/> one).</summary>
    public bool TryAcquire(TKey key, out TValue value)
    {
        ref (TValue Value, int Refs) entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
        if (Unsafe.IsNullRef(ref entry))
        {
            value = default!;
            return false;
        }

        entry.Refs++;
        value = entry.Value;
        return true;
    }

    /// <summary>A new value with one reference.</summary>
    public void Add(TKey key, TValue value)
    {
        _entries.Add(key, (value, 1));
    }

    /// <summary>Drops a reference; true, with the value, when that was the last one and the entry is gone.</summary>
    public bool Release(TKey key, out TValue value)
    {
        ref (TValue Value, int Refs) entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
        if (Unsafe.IsNullRef(ref entry) || --entry.Refs > 0)
        {
            value = default!;
            return false;
        }

        value = entry.Value;
        _entries.Remove(key);
        return true;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        bool found = _entries.TryGetValue(key, out (TValue Value, int Refs) entry);
        value = entry.Value;
        return found;
    }

    public bool Contains(TKey key)
    {
        return _entries.ContainsKey(key);
    }

    /// <summary>How many references the key holds; 0 when it is not here.</summary>
    public int RefsOf(TKey key)
    {
        return _entries.TryGetValue(key, out (TValue Value, int Refs) entry) ? entry.Refs : 0;
    }

    /// <summary>Replaces the value of an existing entry, keeping its references.</summary>
    public void Set(TKey key, TValue value)
    {
        ref (TValue Value, int Refs) entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
        if (!Unsafe.IsNullRef(ref entry))
            entry.Value = value;
    }
}
