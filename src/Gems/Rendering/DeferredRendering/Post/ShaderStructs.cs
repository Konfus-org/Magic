using System.Runtime.InteropServices;

namespace Magic.DeferredRendering.Post;

/// <summary>
/// One pass of a pixel effect, the twin of <c>ApplyPixelEffect.slang</c>'s: the heap id of the effects' params table
/// and the effect's row in it, the level of its target the pass fills, 0 for its apply, and the heap ids of the picture
/// it reads, of what it writes, and of the effect's target and history, with whether the history holds last frame's
/// target, and of the sampler that filters between texels.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuEffectPass
{
    public uint Rows;

    public uint Row;

    public uint Level;

    public uint Picture;

    public uint Output;

    public uint Target;

    public uint History;

    public uint HasHistory;

    public uint Sampler;
}
