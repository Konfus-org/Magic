using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Assets;

/// <summary>
/// The params of a <see cref="Material"/>, by the names of its shader's members:
/// <c>material.Params["roughness"] = 0.8f</c>.
/// </summary>
/// <remarks>
/// Set params only on the game thread; you can set them while the asset is in use. Read them from any thread. Each
/// change replaces the whole snapshot of values, so a reader on another thread never sees a half-made change. Each
/// change also adds one to <see cref="Version"/>, which a reader compares with the count it last read. Setting a param
/// to the value it has counts nothing. As JSON, it is an object of params by name, <c>{ "roughness": { "x": 0.8 }
/// }</c>.
/// </remarks>
public sealed class MaterialParamDictionary
    : IDictionary<string, MaterialParam>,
        IReadOnlyDictionary<string, MaterialParam>
{
    private ImmutableDictionary<string, MaterialParam> _values = ImmutableDictionary.Create<string, MaterialParam>(
        StringComparer.Ordinal
    );

    private long _version;

    public int Count => _values.Count;

    public bool IsReadOnly => false;

    public ICollection<string> Keys => ((IDictionary<string, MaterialParam>)_values).Keys;

    public ICollection<MaterialParam> Values => ((IDictionary<string, MaterialParam>)_values).Values;

    /// <summary>
    /// How many times it has changed.
    /// </summary>
    /// <remarks>
    /// Read it before the values. A change counts only after its values are in, so the values that you read after it
    /// are at least as new as it says.
    /// </remarks>
    public long Version => Volatile.Read(ref _version);

    IEnumerable<string> IReadOnlyDictionary<string, MaterialParam>.Keys => _values.Keys;

    IEnumerable<MaterialParam> IReadOnlyDictionary<string, MaterialParam>.Values => _values.Values;

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

    public void CopyTo(KeyValuePair<string, MaterialParam>[] array, int arrayIndex) =>
        ((ICollection<KeyValuePair<string, MaterialParam>>)_values).CopyTo(array, arrayIndex);

    /// <summary>
    /// Its params as they are now, a snapshot nothing changes as it is walked.
    /// </summary>
    public IEnumerator<KeyValuePair<string, MaterialParam>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, MaterialParam>>)_values).GetEnumerator();

    public bool Remove(KeyValuePair<string, MaterialParam> item) => Contains(item) && Remove(item.Key);

    public bool Remove(string key)
    {
        int count = _values.Count;
        Change(_values.Remove(key));

        return _values.Count < count;
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out MaterialParam value) =>
        _values.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Takes <paramref name="values"/> in place of the snapshot, then counts the change, unless nothing changed.
    /// </summary>
    /// <remarks>
    /// Only the game thread calls it, so it is enough to write the count after the values.
    /// </remarks>
    private void Change(ImmutableDictionary<string, MaterialParam> values)
    {
        if (ReferenceEquals(values, _values))
            return;

        _values = values;
        Volatile.Write(ref _version, _version + 1);
    }
}
