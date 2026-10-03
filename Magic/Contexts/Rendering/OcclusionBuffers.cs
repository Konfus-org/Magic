using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// What a view has only when occlusion is on: the late pass's buffers and two depth pyramids (floats, level after
/// level) used in turn: <see cref="ThisFrame"/> is built this frame, <see cref="Previous"/> was built last frame.
/// </summary>
internal sealed class OcclusionBuffers
{
    private readonly GrowableBuffer[] _pyramids;
    private int _current;

    public OcclusionBuffers(IRendering gpu)
    {
        const GpuBufferUsage rw = GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite;
        DrawArgsLate = new GrowableBuffer(gpu, GpuBufferUsage.Indirect | rw, Buckets.ChunkBytes);
        Candidates = new GrowableBuffer(gpu, rw, 1024);
        DispatchArgsLate = new GrowableBuffer(gpu, GpuBufferUsage.Indirect | rw, 16);
        _pyramids = [new GrowableBuffer(gpu, rw, 4096), new GrowableBuffer(gpu, rw, 4096)];
    }

    public GrowableBuffer DrawArgsLate { get; }

    /// <summary>
    /// What the early pass held back for the late pass: [0] counts them, their slots follow.
    /// </summary>
    public GrowableBuffer Candidates { get; }

    public GrowableBuffer DispatchArgsLate { get; }

    /// <summary>
    /// The two depth pyramids.
    /// </summary>
    public ReadOnlySpan<GrowableBuffer> Pyramids => _pyramids;

    public GrowableBuffer Previous => _pyramids[_current ^ 1];

    public GrowableBuffer ThisFrame => _pyramids[_current];

    /// <summary>
    /// A new frame: last frame's "current" pyramid becomes "previous".
    /// </summary>
    public void TurnOver()
    {
        _current ^= 1;
    }
}
