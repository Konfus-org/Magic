using System.Runtime.InteropServices;

namespace Magic.GlobalIllumination;

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
