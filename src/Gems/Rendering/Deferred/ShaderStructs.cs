using System.Runtime.InteropServices;
using Magic.Graphics;

namespace Magic.Deferred;

/// <summary>
/// What the cluster passes read beyond the frame, the twin of <c>Scene/Clusters.slang</c>'s: the cull's limits and
/// buffers, the depth pyramid, the visibility ids and the impostor handoff, written once a frame into the constants
/// buffer after <see cref="GpuIndirectLight"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ClusterConstants
{
    public const int Offset = GpuIndirectLight.Offset + GpuIndirectLight.Bytes;

    public const int Bytes = 64;

    public uint HzbLevels;

    public uint HasHzb;

    public float ErrorThreshold;

    public float ImpostorPixels;

    public float ImpostorDistance;

    public uint MaxVisibleClusters;

    public uint Clusters;

    public uint Models;

    public uint VisibleClusters;

    public uint DrawArguments;

    public uint Counts;

    public uint RetestList;

    public uint RetestArguments;

    public uint Hzb;

    public uint Visibility;

    public uint GlassVisibility;
}

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
