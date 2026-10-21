using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// A <see cref="GpuTable{T}"/> whatever its rows, as <see cref="GpuResources"/> sends every table in one loop and finds
/// it as it stands.
/// </summary>
internal interface IGpuTable
{
    BoundResource Bound { get; }

    void Upload();

    void Release();
}

/// <summary>
/// A GPU buffer of <typeparamref name="T"/> rows that grows, with a copy of every row on the CPU: rows are set on the
/// copy, and <see cref="Upload"/> sends the rows that changed, a run of neighbours at a time, so a frame that moves a
/// few instances of many sends those few, wherever they are. Growing makes the buffer again twice as big and uploads
/// the whole copy into it, since uploads reach the GPU in the order they are made, where a copy on the GPU would run
/// after every upload of its frame and undo them. Its handle changes as it grows, so whoever binds it asks for it each
/// frame. Render thread.
/// </summary>
internal sealed class GpuTable<T> : IGpuTable
    where T : unmanaged
{
    /// <summary>
    /// Rows apart two changed rows may be and still go up as one run, the rows between them with them: fewer, larger
    /// uploads.
    /// </summary>
    private const int Gap = 16;

    /// <summary>
    /// Runs past which an upload sends everything from the first changed row to the last as one, as a frame that
    /// changed that much changed most of it.
    /// </summary>
    private const int MaxRuns = 256;

    private readonly IGpu _gpu;
    private readonly GpuBufferUsage _usage;
    private readonly List<int> _dirty = [];
    private T[] _rows;
    private bool[] _isDirty;
    private bool _isAllDirty = true;

    internal GpuTable(IGpu gpu, GpuBufferUsage usage, int rows)
    {
        _gpu = gpu;
        _usage = usage;
        _rows = new T[Math.Max(rows, 1)];
        _isDirty = new bool[_rows.Length];
        Buffer = gpu.CreateBuffer(usage, Bytes);
    }

    public BoundResource Bound => BoundResource.Of(Buffer, Bytes);

    internal static int RowBytes => Unsafe.SizeOf<T>();

    internal GpuBuffer Buffer { get; private set; }

    internal int Capacity => _rows.Length;

    /// <summary>
    /// The rows as last set.
    /// </summary>
    internal ReadOnlySpan<T> Rows => _rows;

    internal uint Bytes => (uint)(_rows.Length * RowBytes);

    /// <summary>
    /// Releases the buffer: the manager's, when what holds the table goes.
    /// </summary>
    public void Release()
    {
        _gpu.Release(Buffer);
    }

    /// <summary>
    /// Sends the rows set since the last upload, a run of neighbours at a time; they reach the buffer at the start of
    /// the next submit.
    /// </summary>
    public void Upload()
    {
        if (_isAllDirty)
        {
            _gpu.Upload(Buffer, 0, MemoryMarshal.AsBytes(_rows.AsSpan()));
            Clean();
            return;
        }

        if (_dirty.Count == 0) return;

        _dirty.Sort();
        int runs = 1;
        for (int i = 1; i < _dirty.Count; i++)
        {
            runs += _dirty[i] - _dirty[i - 1] > Gap ? 1 : 0;
        }

        if (runs > MaxRuns)
        {
            Send(_dirty[0], _dirty[^1] + 1);
            Clean();
            return;
        }

        int start = _dirty[0];
        for (int i = 1; i <= _dirty.Count; i++)
        {
            if (i == _dirty.Count || _dirty[i] - _dirty[i - 1] > Gap)
            {
                Send(start, _dirty[i - 1] + 1);
                start = i < _dirty.Count ? _dirty[i] : start;
            }
        }

        Clean();
    }

    internal void Set(int row, in T value)
    {
        Grow(row + 1);
        _rows[row] = value;
        Mark(row);
    }

    /// <summary>
    /// Makes room for <paramref name="rows"/>: a new buffer, all of whose rows the next upload sends.
    /// </summary>
    internal void Grow(int rows)
    {
        if (rows <= _rows.Length) return;

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)rows);
        Array.Resize(ref _rows, capacity);
        Array.Resize(ref _isDirty, capacity);
        _gpu.Release(Buffer);
        Buffer = _gpu.CreateBuffer(_usage, Bytes);
        _isAllDirty = true;
    }

    private void Mark(int row)
    {
        if (_isAllDirty || _isDirty[row]) return;

        _isDirty[row] = true;
        _dirty.Add(row);
    }

    private void Send(int start, int end)
    {
        _gpu.Upload(Buffer, (uint)(start * RowBytes), MemoryMarshal.AsBytes(_rows.AsSpan(start, end - start)));
    }

    private void Clean()
    {
        foreach (int row in _dirty)
        {
            _isDirty[row] = false;
        }

        _dirty.Clear();
        _isAllDirty = false;
    }
}
