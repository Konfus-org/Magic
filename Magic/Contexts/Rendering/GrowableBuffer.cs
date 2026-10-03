using Magic.Interfaces;
using System.Numerics;

namespace Magic.Contexts.Rendering;

/// <summary>
/// A GPU buffer and its size that can be made bigger. Growing makes a new buffer (a power of two) and releases the old
/// one, contents and all: whoever fills it uploads again, which every user does anyway (from a CPU mirror, or because the
/// GPU rewrites it every frame).
/// </summary>
internal sealed class GrowableBuffer
{
    public GrowableBuffer(IRendering gpu, GpuBufferUsage usage, uint bytes)
    {
        Usage = usage;
        Size = BitOperations.RoundUpToPowerOf2(Math.Max(16, bytes));
        Handle = gpu.CreateBuffer(usage, Size);
    }

    public GpuBufferUsage Usage { get; }

    public GpuBuffer Handle { get; private set; }

    public uint Size { get; private set; }

    /// <summary>
    /// Makes sure <paramref name="bytes"/> fit; true when the buffer was replaced (its contents are gone).
    /// </summary>
    public bool Ensure(IRendering gpu, uint bytes)
    {
        if (bytes <= Size)
            return false;

        gpu.Release(Handle);
        Size = BitOperations.RoundUpToPowerOf2(bytes);
        Handle = gpu.CreateBuffer(Usage, Size);
        return true;
    }
}
