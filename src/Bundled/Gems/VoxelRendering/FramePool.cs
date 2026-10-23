using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.VoxelRendering;

/// <summary>
/// The textures and buffers a frame asks for (<see cref="IRendering.Texture"/>, <see cref="IRendering.Buffer"/>), each
/// this frame's alone: one the same as it that an earlier frame asked for is handed out again, its contents what that
/// frame left, else one is made, a buffer filled with zeros; one no frame asked for is let go as the frame ends. The
/// samplers asked for are made once and kept. Render thread.
/// </summary>
internal sealed class FramePool(IGpuDriver gpu) : IDisposable
{
    private const GpuBufferUsage BufferUsage = GpuBufferUsage.GraphicsRead
        | GpuBufferUsage.ComputeRead
        | GpuBufferUsage.ComputeWrite
        | GpuBufferUsage.Arguments;

    private readonly Dictionary<GpuTextureDescription, Kept<GpuTexture>> _textures = [];
    private readonly Dictionary<uint, Kept<GpuBuffer>> _buffers = [];
    private readonly Dictionary<GpuSamplerDescription, GpuSampler> _samplers = [];

    /// <summary>
    /// Zeros as many bytes as the largest buffer made, which a new buffer is filled with.
    /// </summary>
    private byte[] _zeros = [];

    public void Dispose()
    {
        foreach (Kept<GpuTexture> kept in _textures.Values)
        {
            foreach (GpuTexture texture in kept.Made)
            {
                gpu.Release(texture);
            }
        }

        foreach (Kept<GpuBuffer> kept in _buffers.Values)
        {
            foreach (GpuBuffer buffer in kept.Made)
            {
                gpu.Release(buffer);
            }
        }

        foreach (GpuSampler sampler in _samplers.Values)
        {
            gpu.Release(sampler);
        }
    }

    internal GpuTexture Texture(in GpuTextureDescription description)
    {
        Kept<GpuTexture> kept = GetKept(_textures, description);
        if (kept.Taken == kept.Made.Count) kept.Made.Add(gpu.CreateTexture(description));

        return kept.Made[kept.Taken++];
    }

    internal GpuBuffer Buffer(uint bytes)
    {
        Kept<GpuBuffer> kept = GetKept(_buffers, bytes);
        if (kept.Taken == kept.Made.Count) kept.Made.Add(MakeBuffer(bytes));

        return kept.Made[kept.Taken++];
    }

    internal GpuSampler Sampler(in GpuSamplerDescription description)
    {
        if (!_samplers.TryGetValue(description, out GpuSampler made))
        {
            made = gpu.CreateSampler(description);
            _samplers[description] = made;
        }

        return made;
    }

    /// <summary>
    /// Ends the frame: of each description, those past what the frame took are let go, and the next frame takes from
    /// the first.
    /// </summary>
    internal void EndFrame()
    {
        foreach (Kept<GpuTexture> kept in _textures.Values)
        {
            for (int i = kept.Taken; i < kept.Made.Count; i++)
            {
                gpu.Release(kept.Made[i]);
            }

            kept.EndFrame();
        }

        foreach (Kept<GpuBuffer> kept in _buffers.Values)
        {
            for (int i = kept.Taken; i < kept.Made.Count; i++)
            {
                gpu.Release(kept.Made[i]);
            }

            kept.EndFrame();
        }
    }

    private static Kept<T> GetKept<TKey, T>(Dictionary<TKey, Kept<T>> kept, TKey key)
        where TKey : notnull
    {
        if (!kept.TryGetValue(key, out Kept<T>? found))
        {
            found = new Kept<T>();
            kept[key] = found;
        }

        return found;
    }

    private GpuBuffer MakeBuffer(uint bytes)
    {
        uint size = Math.Max(bytes, 4);
        GpuBuffer buffer = gpu.CreateBuffer(BufferUsage, size);
        if (_zeros.Length < size) _zeros = new byte[size];

        gpu.Upload(buffer, 0, _zeros.AsSpan(0, (int)size));

        return buffer;
    }

    /// <summary>
    /// The resources of one description: all made so far, and how many of them the frame has taken.
    /// </summary>
    private sealed class Kept<T>
    {
        internal List<T> Made { get; } = [];

        internal int Taken { get; set; }

        internal void EndFrame()
        {
            Made.RemoveRange(Taken, Made.Count - Taken);
            Taken = 0;
        }
    }
}
