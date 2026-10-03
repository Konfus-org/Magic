namespace Magic.Utils;

/// <summary>
/// Values in numbered slots that never move: <see cref="Add"/> takes a slot given back by <see cref="Remove"/> before
/// a new one, so a slot number stays good for as long as its value is here and the numbers stay dense. What the GPU
/// indexes by slot (material records, meshes) is kept in these. Not thread-safe.
/// </summary>
internal sealed class Slots<T> where T : class
{
    private readonly List<T?> _values = [];
    private readonly Stack<uint> _free = [];

    /// <summary>
    /// One past the highest slot ever handed out; slots below it may be empty.
    /// </summary>
    public int Count => _values.Count;

    /// <summary>
    /// The slots handed out and not given back.
    /// </summary>
    public int Used => _values.Count - _free.Count;

    /// <summary>
    /// The value in the slot; null for one that is empty.
    /// </summary>
    public T? this[uint slot]
    {
        get => _values[(int)slot];
        set => _values[(int)slot] = value;
    }

    /// <summary>
    /// A slot holding <paramref name="value"/>; null reserves one to be filled later.
    /// </summary>
    public uint Add(T? value = null)
    {
        if (_free.Count > 0)
        {
            uint reused = _free.Pop();
            _values[(int)reused] = value;
            return reused;
        }

        _values.Add(value);
        return (uint)_values.Count - 1;
    }

    /// <summary>
    /// Empties the slot and gives it back.
    /// </summary>
    public void Remove(uint slot)
    {
        _values[(int)slot] = null;
        _free.Push(slot);
    }
}
