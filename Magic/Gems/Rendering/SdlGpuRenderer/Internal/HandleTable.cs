namespace SdlGpuRendererGem;

/// <summary>
/// SDL objects by handle id: dense, id 0 never used, freed ids used again.
/// </summary>
internal sealed class HandleTable<T> where T : struct
{
    private readonly List<T> _items = [default];
    private readonly List<bool> _alive = [false];
    private readonly Stack<uint> _free = [];

    public T this[uint id] => id < _items.Count ? _items[(int)id] : default;

    public IEnumerable<T> Live => _items.Where((_, i) => _alive[i]);

    public uint Add(T item)
    {
        if (_free.TryPop(out uint id))
        {
            _items[(int)id] = item;
            _alive[(int)id] = true;
            return id;
        }

        _items.Add(item);
        _alive.Add(true);
        return (uint)(_items.Count - 1);
    }

    /// <summary>
    /// Forgets every id without handing any out again: one held from before reads and frees as nothing.
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i] = default;
            _alive[i] = false;
        }

        _free.Clear();
    }

    /// <summary>
    /// Forgets the id, handing back what it held.
    /// </summary>
    public T Free(uint id)
    {
        if (id == 0 || id >= _items.Count || !_alive[(int)id])
            return default;

        T item = _items[(int)id];
        _items[(int)id] = default;
        _alive[(int)id] = false;
        _free.Push(id);
        return item;
    }
}
