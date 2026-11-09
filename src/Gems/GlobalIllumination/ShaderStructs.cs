using System.Runtime.InteropServices;

namespace Magic.GlobalIllumination;

/// <summary>
/// What the gem writes of the library's <c>Light/Indirect.slang</c> IndirectLight each frame: the frame it is for and
/// the ids it names. The grid's placement after it is the GPU's.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct IndirectLight
{
    /// <summary>
    /// The frame's index plus 1, so a buffer never written is for no frame.
    /// </summary>
    public uint FrameNumber;

    public uint Diffuse;

    public uint Reflections;

    public uint Padding0;

    public ProbeGridConstants Grid;
}

/// <summary>
/// The probe grid's size and the heap ids of its buffers: the twin of the first part of the library's
/// <c>Light/Probes.slang</c> ProbeGridConstants, whose placement the GPU writes after it.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ProbeGridConstants
{
    /// <summary>
    /// Probes along x; with the two after, the twin of <c>dimensions</c>.
    /// </summary>
    public uint DimensionX;

    public uint DimensionY;

    public uint DimensionZ;

    public uint Probes;

    public uint Depth;

    public uint DepthSampler;

    public uint Rays;

    public uint RayList;

    public uint RayArguments;

    public uint RayCounts;

    public uint Variance;

    /// <summary>
    /// 0 when the probes start afresh: the GPU starts them, and places the grid at once, at the next frame it places
    /// the grid.
    /// </summary>
    public uint HasHistory;
}

/// <summary>
/// The gem's own constants, the pass header's y: the traced diffuse light's, then the traced reflections'.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GIConstants
{
    public const int Bytes = 80;

    public TracedDiffuseConstants Diffuse;

    public TracedReflectionConstants Reflections;
}

/// <summary>
/// The heap ids of the traced diffuse light's textures and sampler, the twin of <c>Scene/TracedDiffuse.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedDiffuseConstants
{
    public uint Traced;

    public uint Filtered;

    public uint Light;

    public uint Depth;

    public uint LightHistory;

    public uint DepthHistory;

    public uint Sampler;

    /// <summary>
    /// 0 while the history holds nothing of the view: this frame starts it afresh.
    /// </summary>
    public uint HasHistory;
}

/// <summary>
/// The heap ids of the traced reflections' textures and sampler, the twin of <c>Scene/TracedReflections.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedReflectionConstants
{
    public uint Traced;

    public uint Rays;

    public uint Resolved;

    public uint Distance;

    public uint Light;

    public uint Depth;

    public uint LightHistory;

    public uint DepthHistory;

    public uint Sampler;

    /// <summary>
    /// 0 while the history holds nothing of the view: this frame starts it afresh.
    /// </summary>
    public uint HasHistory;

    public uint Padding0;

    public uint Padding1;
}
