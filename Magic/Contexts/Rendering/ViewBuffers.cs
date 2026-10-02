using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// One view's GPU buffers: what the culling passes fill and the draws read, plus, when occlusion is on, the late pass's
/// buffers and two depth pyramids (floats, level after level) used in turn: <see cref="HiZCurrent"/> is built this
/// frame, the other was built last frame. The GPU rewrites all of them every frame, so growing one loses nothing.
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
        if (!occlusion)
        {
            HiZ = [];
            return;
        }

        DrawArgsLate = new GrowableBuffer(gpu, GpuBufferUsage.Indirect | rw, Buckets.ChunkBytes);
        Candidates = new GrowableBuffer(gpu, rw, 1024);
        DispatchArgsLate = new GrowableBuffer(gpu, GpuBufferUsage.Indirect | rw, 16);
        HiZ = [new GrowableBuffer(gpu, rw, 4096), new GrowableBuffer(gpu, rw, 4096)];
    }

    public GrowableBuffer DrawArgsEarly { get; }

    public GrowableBuffer? DrawArgsLate { get; }

    public GrowableBuffer VisibleIds { get; }

    /// <summary>What the early pass held back for the late pass: [0] counts them, their slots follow. None without occlusion.</summary>
    public GrowableBuffer? Candidates { get; }

    public GrowableBuffer VisiblePages { get; }

    /// <summary>The early instance cull's indirect dispatch: x counts the visible pages, y = z = 1.</summary>
    public GrowableBuffer DispatchArgs { get; }

    public GrowableBuffer? DispatchArgsLate { get; }

    /// <summary>The two depth pyramids; none without occlusion.</summary>
    public GrowableBuffer[] HiZ { get; }

    public int HiZWidth { get; set; }

    public int HiZHeight { get; set; }

    public int HiZLevels { get; set; }

    public int HiZCurrent { get; set; }

    public GrowableBuffer? HiZPrevious => HiZ.Length == 0 ? null : HiZ[HiZCurrent ^ 1];

    public GrowableBuffer? HiZThisFrame => HiZ.Length == 0 ? null : HiZ[HiZCurrent];

    /// <summary>The pyramid's size as the frame constants carry it.</summary>
    public (int Width, int Height, int Levels) HiZSize => (HiZWidth, HiZHeight, HiZLevels);
}
