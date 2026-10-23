using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Contexts.Assets;

/// <summary>
/// The params of a <see cref="Material"/>, by the names of its shader's members:
/// <c>material.Params["roughness"] = 0.8f</c>. A script may set them while the asset is in use, on the main
/// thread, and whatever draws with them reads them from any thread. Its values are a snapshot that never changes, which
/// each change replaces whole, so a reader on another thread never sees one half made; and each change counts in
/// <see cref="Version"/>, which whatever draws from it compares with the count it last read, so nothing has to tell it.
/// Setting a param to the value it has counts nothing. As JSON, an object of params by name,
/// <c>{ "roughness": { "x": 0.8 } }</c>.
/// </summary>
public sealed class MaterialParamDictionary
    : IDictionary<string, MaterialParam>, IReadOnlyDictionary<string, MaterialParam>
{
    private ImmutableDictionary<string, MaterialParam> _values
        = ImmutableDictionary.Create<string, MaterialParam>(StringComparer.Ordinal);

    private long _version;

    public int Count => _values.Count;

    public bool IsReadOnly => false;

    public ICollection<string> Keys => ((IDictionary<string, MaterialParam>)_values).Keys;

    IEnumerable<string> IReadOnlyDictionary<string, MaterialParam>.Keys => _values.Keys;

    public ICollection<MaterialParam> Values => ((IDictionary<string, MaterialParam>)_values).Values;

    IEnumerable<MaterialParam> IReadOnlyDictionary<string, MaterialParam>.Values => _values.Values;

    /// <summary>
    /// How many times it has changed. Read it before the values, as a change counts once its values are in: what is
    /// read after it is at least as new as it says.
    /// </summary>
    public long Version => Volatile.Read(ref _version);

    public MaterialParam this[string key]
    {
        get => _values[key];
        set => Change(_values.SetItem(key, value));
    }

    public void Add(KeyValuePair<string, MaterialParam> item) => Add(item.Key, item.Value);

    public void Add(string key, MaterialParam value) => Change(_values.Add(key, value));

    public void Clear() => Change(_values.Clear());

    public bool Contains(KeyValuePair<string, MaterialParam> item) => _values.Contains(item);

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, MaterialParam>[] array, int arrayIndex)
        => ((ICollection<KeyValuePair<string, MaterialParam>>)_values).CopyTo(array, arrayIndex);

    /// <summary>
    /// Its params as they are now, a snapshot nothing changes as it is walked.
    /// </summary>
    public IEnumerator<KeyValuePair<string, MaterialParam>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, MaterialParam>>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Remove(KeyValuePair<string, MaterialParam> item) => Contains(item) && Remove(item.Key);

    public bool Remove(string key)
    {
        int count = _values.Count;
        Change(_values.Remove(key));

        return _values.Count < count;
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out MaterialParam value)
        => _values.TryGetValue(key, out value);

    /// <summary>
    /// Takes <paramref name="values"/> in place of the snapshot, then counts the change, unless nothing changed. The
    /// main thread's alone, so the count needs no more than to be written after the values.
    /// </summary>
    private void Change(ImmutableDictionary<string, MaterialParam> values)
    {
        if (ReferenceEquals(values, _values)) return;

        _values = values;
        Volatile.Write(ref _version, _version + 1);
    }
}
