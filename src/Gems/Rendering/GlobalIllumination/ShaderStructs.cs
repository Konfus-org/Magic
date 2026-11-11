using System.Runtime.InteropServices;

namespace Magic.GlobalIllumination;

/// <summary>
/// The gem's own constants, the pass header's y, the twin of <c>Scene/Traced.slang</c>'s: the traced diffuse light's,
/// the traced reflections', and what both read their history with.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GIConstants
{
    public TracedDiffuseConstants Diffuse;

    public TracedReflectionConstants Reflections;

    public uint Sampler;

    /// <summary>
    /// 0 while the history holds nothing of the view: this frame starts it afresh.
    /// </summary>
    public uint HasHistory;

    /// <summary>
    /// The traced textures' size.
    /// </summary>
    public uint TracedWidth;

    public uint TracedHeight;
}

/// <summary>
/// The heap ids of the traced diffuse light's textures, the twin of <c>Scene/Traced.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedDiffuseConstants
{
    public uint Traced;

    public uint Filtered;

    public TracedBlendConstants Blend;
}

/// <summary>
/// The heap ids of the traced reflections' textures, the twin of <c>Scene/Traced.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedReflectionConstants
{
    public uint Traced;

    public uint Rays;

    public uint Resolved;

    public uint Distance;

    public TracedBlendConstants Blend;
}

/// <summary>
/// The heap ids of what a traced effect blends over frames, the twin of <c>Scene/Traced.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedBlendConstants
{
    public uint Light;

    public uint Depth;

    public uint LightHistory;

    public uint DepthHistory;
}
