
namespace Magic.ClusterRendering;

/// <summary>
/// Hands out runs of a pool of rows, first fit, and takes them back, merging free runs that touch; the pool grows by
/// what no free run holds. <see cref="End"/> is how many rows the pool needs.
/// </summary>
internal sealed class RangeAllocator
{
    /// <summary>
    /// The free runs, by start, none touching another.
    /// </summary>
    private readonly SortedList<int, int> _free = [];

    public int End { get; private set; }

    /// <summary>
    /// The start of a run of <paramref name="count"/> rows.
    /// </summary>
    public int Allocate(int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        for (int i = 0; i < _free.Count; i++)
        {
            int start = _free.Keys[i];
            int length = _free.Values[i];
            if (length < count)
            {
                continue;
            }

            _free.RemoveAt(i);
            if (length > count)
            {
                _free.Add(start + count, length - count);
            }

            return start;
        }

        int end = End;
        End += count;
        return end;
    }

    /// <summary>
    /// Gives back the run of <paramref name="count"/> rows at <paramref name="start"/>.
    /// </summary>
    public void Free(int start, int count)
    {
        if (count <= 0)
        {
            return;
        }

        int index = 0;
        while (index < _free.Count && _free.Keys[index] < start)
        {
            index++;
        }

        if (index < _free.Count && start + count == _free.Keys[index])
        {
            count += _free.Values[index];
            _free.RemoveAt(index);
        }

        if (index > 0 && _free.Keys[index - 1] + _free.Values[index - 1] == start)
        {
            start = _free.Keys[index - 1];
            count += _free.Values[index - 1];
            _free.RemoveAt(index - 1);
        }

        if (start + count == End)
        {
            End = start;
            return;
        }

        _free.Add(start, count);
    }
}
