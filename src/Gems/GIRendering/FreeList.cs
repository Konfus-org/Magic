namespace Magic.GIRendering;

/// <summary>
/// The free ranges of a pool of <c>capacity</c> elements: a free list, handing out ranges first fit and merging a range
/// given back with the free ranges it touches.
/// </summary>
internal sealed class FreeList(int capacity)
{
    /// <summary>
    /// The free ranges, by start, none touching the next.
    /// </summary>
    private readonly List<(int Start, int Length)> _free = [(0, capacity)];

    /// <summary>
    /// Whether a free range holds <paramref name="length"/> elements.
    /// </summary>
    public bool CanAllocate(int length) => _free.Exists(range => range.Length >= length);

    /// <summary>
    /// The start of <paramref name="length"/> elements taken from the first free range that holds them, which
    /// <see cref="CanAllocate"/> says there is.
    /// </summary>
    public int Allocate(int length)
    {
        for (int i = 0; i < _free.Count; i++)
        {
            (int start, int free) = _free[i];
            if (free < length)
                continue;

            if (free == length)
                _free.RemoveAt(i);
            else
                _free[i] = (start + length, free - length);

            return start;
        }

        throw new InvalidOperationException($"No free range holds {length} elements.");
    }

    /// <summary>
    /// Gives back the <paramref name="length"/> elements from <paramref name="start"/> that <see cref="Allocate"/> took.
    /// </summary>
    public void Free(int start, int length)
    {
        int at = 0;
        while (at < _free.Count && _free[at].Start < start)
        {
            at++;
        }

        _free.Insert(at, (start, length));

        // Merges it with the range after, then with the range before, where they touch.
        if (at + 1 < _free.Count && start + length == _free[at + 1].Start)
        {
            _free[at] = (start, length + _free[at + 1].Length);
            _free.RemoveAt(at + 1);
        }

        if (at > 0 && _free[at - 1].Start + _free[at - 1].Length == start)
        {
            _free[at - 1] = (_free[at - 1].Start, _free[at - 1].Length + _free[at].Length);
            _free.RemoveAt(at);
        }
    }
}
