using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

/// <summary>
/// Values by key, each with a reference count. <see cref="Add"/> puts a value in with one reference,
/// <see cref="TryAcquire"/> takes another, <see cref="Release"/> drops one and hands the value back when it was the last
/// (so the caller frees what it holds). A value is replaced whole with <see cref="Set"/>, never changed in place. Not
/// thread-safe. The render tables (materials, textures, models, pipelines, passes) are each one of these, with what else
/// they hold beside it.
/// </summary>
internal class RefCountTable<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, (TValue Value, int Refs)> _entries = [];

    public int Count => _entries.Count;

    /// <summary>
    /// Every key with its value, walked by a foreach without allocating.
    /// </summary>
    public EntryEnumerator Entries => new(_entries);

    /// <summary>
    /// Takes another reference to an existing value; false when there is none (then <see cref="Add"/> one).
    /// </summary>
    public bool TryAcquire(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ref (TValue Value, int Refs) entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
        if (Unsafe.IsNullRef(ref entry))
        {
            value = default;
            return false;
        }

        entry.Refs++;
        value = entry.Value;
        return true;
    }

    /// <summary>
    /// A new value with one reference.
    /// </summary>
    public void Add(TKey key, TValue value)
    {
        _entries.Add(key, (value, 1));
    }

    /// <summary>
    /// Drops a reference; true, with the value, when that was the last one and the entry is gone.
    /// </summary>
    public bool Release(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ref (TValue Value, int Refs) entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
        if (Unsafe.IsNullRef(ref entry) || --entry.Refs > 0)
        {
            value = default;
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

    /// <summary>
    /// How many references the key holds; 0 when it is not here.
    /// </summary>
    public int RefsOf(TKey key)
    {
        return _entries.TryGetValue(key, out (TValue Value, int Refs) entry) ? entry.Refs : 0;
    }

    /// <summary>
    /// Replaces the value of an existing entry, keeping its references.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        ref (TValue Value, int Refs) entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
        if (!Unsafe.IsNullRef(ref entry))
            entry.Value = value;
    }

    /// <summary>
    /// <see cref="Entries"/>: each key with its value, for a foreach over the table, which must not gain or lose an entry
    /// meanwhile; a walk that may change it takes <see cref="ToArray"/> first.
    /// </summary>
    public struct EntryEnumerator(Dictionary<TKey, (TValue Value, int Refs)> entries)
    {
        private Dictionary<TKey, (TValue Value, int Refs)>.Enumerator _entries = entries.GetEnumerator();

        public readonly (TKey Key, TValue Value) Current => (_entries.Current.Key, _entries.Current.Value.Value);

        public readonly EntryEnumerator GetEnumerator()
        {
            return this;
        }

        public bool MoveNext()
        {
            return _entries.MoveNext();
        }

        /// <summary>
        /// The entries as they are now.
        /// </summary>
        public (TKey Key, TValue Value)[] ToArray()
        {
            List<(TKey Key, TValue Value)> all = [];
            while (MoveNext())
                all.Add(Current);

            return [.. all];
        }
    }
}
