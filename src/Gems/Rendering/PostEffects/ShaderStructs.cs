using System.Runtime.InteropServices;

namespace Magic.PostEffects;

/// <summary>
/// One pass of a pixel effect, the twin of <c>ApplyPixelEffect.slang</c>'s.
/// </summary>
/// <remarks>
/// It holds the heap id of the effects' params table and the effect's row in it, and the level of its target the pass
/// fills, 0 for its apply. It also holds the heap ids of the picture it reads, of what it writes, of the effect's
/// target and history, and of the sampler that filters between texels, and whether the history holds last frame's
/// target.
/// </remarks>
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
