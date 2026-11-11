using Magic.Graphics;
using Magic.Utils;

namespace Magic.GpuWorld;

/// <summary>
/// A pool of rows of <paramref name="rowBytes"/> bytes in a buffer asked for as <paramref name="buffer"/>, with a free
/// list over them, that starts at <paramref name="rows"/> rows and grows with the scene. A range that no free range holds
/// grows it by half, or by what the range needs past its end when that is more; the GPU provider keeps what the buffer
/// holds when it is next asked for at its new size.
/// </summary>
public sealed class BufferPool(BufferDescription buffer, int rowBytes, int rows)
{
    private readonly FreeList _ranges = new(rows);

    private int _rows = rows;

    /// <summary>
    /// The pool's buffer at its size now, asked for every frame, so the GPU provider keeps what it holds. Ask for it
    /// after this frame's <see cref="Allocate"/> calls and before uploading into it or naming it, as one that grew is a
    /// new buffer.
    /// </summary>
    public Result<GpuBuffer> GetBuffer(RenderGraph graph) =>
        graph.Buffer(buffer with { Bytes = (long)_rows * rowBytes });

    /// <summary>
    /// The first of <paramref name="count"/> rows taken, the pool grown first when no free range holds them.
    /// </summary>
    public int Allocate(int count)
    {
        if (!_ranges.CanAllocate(count))
        {
            // The new rows are the free list's end, given back, so they join a free range that ends where they start.
            int grown = Math.Max(_rows + (_rows / 2), _rows + count);
            _ranges.Free(_rows, grown - _rows);
            _rows = grown;
        }

        return _ranges.Allocate(count);
    }

    /// <summary>
    /// Gives back the <paramref name="count"/> rows from <paramref name="start"/> that <see cref="Allocate"/> took.
    /// </summary>
    public void Free(int start, int count) => _ranges.Free(start, count);
}
