using System.Runtime.InteropServices;
using Magic.Graphics;

namespace Magic.GlobalIllumination;

/// <summary>
/// The gem's own constants, the pass header's y, the twin of <c>Traced.slang</c>'s: the traced diffuse light's,
/// the traced reflections', the probe update's, the view depth both blend with, and what both read their history
/// with. Each buffer, texture and sampler is its handle, laid out as the heap id the shader reads.
/// </summary>
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
/// What the probe update reads beyond the grid, the twin of <c>Traced.slang</c>'s: its rays, the list of which probe
/// traces each, the trace's arguments, the probes' ray counts, each probe texel's variance, and how far a probe's depth
/// reaches.
/// </summary>
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
