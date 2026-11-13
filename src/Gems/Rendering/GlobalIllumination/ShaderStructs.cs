using System.Runtime.InteropServices;
using Magic.Graphics;

namespace Magic.GlobalIllumination;

/// <summary>
/// The gem's own constants, the pass header's y, the twin of <c>Traced.slang</c>'s.
/// </summary>
/// <remarks>
/// They hold the traced diffuse light's, the traced reflections' and the probe update's constants, the view depth both
/// blends use, and what both read their history with. Each buffer, texture and sampler is its handle, laid out as the
/// heap id the shader reads.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GIConstants
{
    public TracedDiffuseConstants Diffuse;

    public TracedReflectionConstants Reflections;

    public ProbeUpdateConstants Probes;

    /// <summary>
    /// The view depth of each traced pixel, which the diffuse blend writes, and last frame's, which both blends read.
    /// </summary>
    public GpuTexture Depth;

    public GpuTexture DepthHistory;

    public GpuSampler Sampler;

    /// <summary>
    /// The traced textures' size.
    /// </summary>
    public uint TracedWidth;

    public uint TracedHeight;

    /// <summary>
    /// The diffuse light's rays a traced pixel and how far they reach, the reflections' reach, how much of its history
    /// each keeps a frame, and how much of its rays' light a probe takes.
    /// </summary>
    public uint DiffuseRayCount;

    public float DiffuseRayRange;

    public float ReflectionRayRange;

    public float DiffuseHistory;

    public float ReflectionHistory;

    public float ProbeBlend;
}

/// <summary>
/// The traced diffuse light's textures, the twin of <c>Traced.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedDiffuseConstants
{
    public GpuTexture Traced;

    public GpuTexture Filtered;

    public TracedBlendConstants Blend;
}

/// <summary>
/// The traced reflections' textures, the twin of <c>Traced.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedReflectionConstants
{
    public GpuTexture Traced;

    public GpuTexture Rays;

    public GpuTexture Resolved;

    public GpuTexture Distance;

    public TracedBlendConstants Blend;
}

/// <summary>
/// What a traced effect blends over frames, the twin of <c>Traced.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedBlendConstants
{
    public GpuTexture Light;

    public GpuTexture LightHistory;

    /// <summary>
    /// 0 while the history holds nothing of the view: this frame starts it afresh.
    /// </summary>
    public uint HasHistory;
}

/// <summary>
/// What the probe update reads beyond the grid, the twin of <c>Traced.slang</c>'s.
/// </summary>
/// <remarks>
/// It holds the rays, the list of which probe traces each, the trace's arguments, the probes' ray counts, each probe
/// texel's variance, and how far a probe's depth reaches.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ProbeUpdateConstants
{
    public GpuBuffer Rays;

    public GpuBuffer RayList;

    public GpuBuffer RayArguments;

    public GpuBuffer RayCounts;

    public GpuBuffer Variance;

    public float MaxDistance;
}
