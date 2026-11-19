using System.Runtime.InteropServices;
using Magic.GlobalIllumination;
using Magic.Graphics;

namespace Magic.TracedReflections;

/// <summary>
/// The gem's own constants, the pass header's y, the twin of <c>TraceReflections.slang</c>'s.
/// </summary>
/// <remarks>
/// They hold the reflections' textures, the indirect light's view depth of last frame, which the blend reads, and what
/// it reads its history with. Each texture and sampler is its handle, laid out as the heap id the shader reads.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ReflectionConstants
{
    public TracedReflectionConstants Reflections;

    public GpuTexture DepthHistory;

    public GpuSampler Sampler;

    /// <summary>
    /// The traced textures' size.
    /// </summary>
    public uint TracedWidth;

    public uint TracedHeight;

    /// <summary>
    /// How far the rays reach, and how much of its history the blend keeps a frame.
    /// </summary>
    public float ReflectionRayRange;

    public float ReflectionHistory;

    /// <summary>
    /// Last frame's lit picture, which the screen trace reads, or none.
    /// </summary>
    public GpuTexture Picture;
}

/// <summary>
/// The traced reflections' textures, the twin of <c>TraceReflections.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct TracedReflectionConstants
{
    public GpuTexture Traced;

    public GpuTexture Rays;

    public GpuTexture Resolved;

    public GpuTexture Distance;

    /// <summary>
    /// The buffer of what each traced pixel's ray landed on, <c>ReflectionHit</c> a traced pixel.
    /// </summary>
    public GpuBuffer Hits;

    /// <summary>
    /// The light of each traced pixel's ray from the screen, times how much it counts, which the hits' light adds to.
    /// </summary>
    public GpuTexture Screen;

    public TracedBlendConstants Blend;
}
