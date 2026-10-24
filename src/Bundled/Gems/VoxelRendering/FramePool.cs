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

    public void Dispose()
    {
        foreach (Kept<GpuTexture> kept in _textures.Values)
        {
            foreach (GpuTexture texture in kept.Created)
            {
                gpu.Release(texture);
            }
        }

        foreach (Kept<GpuBuffer> kept in _buffers.Values)
        {
            foreach (GpuBuffer buffer in kept.Created)
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
        if (kept.Taken == kept.Created.Count) kept.Created.Add(gpu.CreateTexture(description));

        return kept.Created[kept.Taken++];
    }

    internal GpuBuffer Buffer(uint bytes)
    {
        Kept<GpuBuffer> kept = GetKept(_buffers, bytes);
        if (kept.Taken == kept.Created.Count) kept.Created.Add(gpu.CreateZeroedBuffer(BufferUsage, bytes));

        return kept.Created[kept.Taken++];
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
            for (int i = kept.Taken; i < kept.Created.Count; i++)
            {
                gpu.Release(kept.Created[i]);
            }

            kept.EndFrame();
        }

        foreach (Kept<GpuBuffer> kept in _buffers.Values)
        {
            for (int i = kept.Taken; i < kept.Created.Count; i++)
            {
                gpu.Release(kept.Created[i]);
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

    /// <summary>
    /// The resources of one description: all made so far, and how many of them the frame has taken.
    /// </summary>
    private sealed class Kept<T>
    {
        internal List<T> Created { get; } = [];

        internal int Taken { get; set; }

        internal void EndFrame()
        {
            Created.RemoveRange(Taken, Created.Count - Taken);
            Taken = 0;
        }
    }
}
