using System.Runtime.InteropServices;
using Magic.Graphics;

namespace Magic.GlobalIllumination;

/// <summary>
/// The gem's own constants, the pass header's y, the twin of <c>Traced.slang</c>'s.
/// </summary>
/// <remarks>
/// They hold the traced diffuse light's and the probe update's constants, and the view depth the sky share's blend
/// uses. Each buffer and texture is its handle, laid out as the heap id the shader reads.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GIConstants
{
    public TracedDiffuseConstants Diffuse;

    public ProbeUpdateConstants Probes;

    /// <summary>
    /// The view depth of each traced pixel, the share of the sky it sees and the frames that share blended, which the
    /// sky share's blend writes, and last frame's, which it reads.
    /// </summary>
    public GpuTexture Depth;

    public GpuTexture DepthHistory;

    /// <summary>
    /// The traced textures' size.
    /// </summary>
    public uint TracedWidth;

    public uint TracedHeight;

    /// <summary>
    /// The diffuse light's rays a traced pixel and how far they reach, the most of its history the sky share keeps a
    /// frame, and how much of its rays' light a probe takes.
    /// </summary>
    public uint DiffuseRayCount;

    public float DiffuseRayRange;

    public float DiffuseHistory;

    public float ProbeBlend;

    /// <summary>
    /// The diffuse rays' terms turned off to measure them: 1, the probes' light at a hit; 2, the sky (PROTO).
    /// </summary>
    public uint DiffuseTermsOff;
}

/// <summary>
/// The traced diffuse light's textures, the twin of <c>Traced.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedDiffuseConstants
{
    public GpuTexture Traced;

    public GpuTexture Radiance;

    public GpuTexture Motion;

    public GpuTexture NormalRoughness;

    public GpuTexture ViewZ;

    public uint HasHistory;
}

/// <summary>
/// What a traced effect blends over frames, the twin of <c>Traced.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct TracedBlendConstants
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
