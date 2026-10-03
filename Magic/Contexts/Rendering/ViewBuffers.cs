using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// One view's GPU buffers: what the culling passes fill and the draws read, the light tiles the lighting fills and reads, plus, when occlusion is on, the late pass's
/// buffers and depth pyramids (<see cref="Occlusion"/>). The GPU rewrites all of them every frame, so growing one loses nothing.
/// </summary>
internal sealed class ViewBuffers
{
    public ViewBuffers(IRendering gpu, bool occlusion)
    {
        const GpuBufferUsage rw = GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite;
        DrawArgsEarly = new GrowableBuffer(gpu, GpuBufferUsage.Indirect | rw, Buckets.ChunkBytes);
        VisibleIds = new GrowableBuffer(gpu, GpuBufferUsage.Vertex | GpuBufferUsage.ComputeWrite, 1024);
        VisiblePages = new GrowableBuffer(gpu, rw, 256 * 4);
        DispatchArgs = new GrowableBuffer(gpu, GpuBufferUsage.Indirect | rw, 16);
        LightTiles = new GrowableBuffer(gpu, rw, 4096);
        LightClusters = new GrowableBuffer(gpu, rw, 4096);
        Occlusion = occlusion ? new OcclusionBuffers(gpu) : null;
    }

    public GrowableBuffer DrawArgsEarly { get; }

    /// <summary>
    /// One <see cref="GpuVisible"/> per instance drawn, in each bucket group's region.
    /// </summary>
    public GrowableBuffer VisibleIds { get; }

    public GrowableBuffer VisiblePages { get; }

    /// <summary>
    /// The early instance cull's indirect dispatch: x counts the visible pages, y = z = 1.
    /// </summary>
    public GrowableBuffer DispatchArgs { get; }

    /// <summary>
    /// The lights that reach each screen tile of the view: a row of uints per tile, its light count, the depths drawn
    /// in it, then the lights' indices (<c>Structs.hlsli</c>).
    /// </summary>
    public GrowableBuffer LightTiles { get; }

    /// <summary>
    /// The lights that reach each depth slice of each tile: a row of uints per cluster, its light count then their indices.
    /// </summary>
    public GrowableBuffer LightClusters { get; }

    /// <summary>
    /// The late pass's buffers and the depth pyramids; null without occlusion.
    /// </summary>
    public OcclusionBuffers? Occlusion { get; }

    public int HiZWidth { get; set; }

    public int HiZHeight { get; set; }

    public int HiZLevels { get; set; }

    /// <summary>
    /// The pyramid's size as the frame constants carry it.
    /// </summary>
    public (int Width, int Height, int Levels) HiZSize => (HiZWidth, HiZHeight, HiZLevels);
}
