using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Graphics;

namespace Magic.ClusteredGeometry;

/// <summary>
/// What the cluster passes read beyond the frame, the twin of <c>Scene/Clusters.slang</c>'s: the cull's limits and
/// buffers, the depth pyramid, the visibility ids and the impostor handoff, written once a frame into the constants
/// buffer after <see cref="GpuIndirectLight"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct ClusterConstants
{
    public const int Offset = GpuIndirectLight.Offset + GpuIndirectLight.Bytes;

    public const int Bytes = 68;

    public uint HzbLevels;

    public uint HasHzb;

    public float ErrorThreshold;

    public float ImpostorPixels;

    public float ImpostorDistance;

    public uint MaxVisibleClusters;

    public uint Clusters;

    public uint Indices;

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
/// A model's clusters in the cluster pool and its row of the impostor table, 0 for none, the twin of
/// <c>Scene/Clusters.slang</c>'s: the row of the cluster model table an instance's model index names.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct GpuClusterModel(uint ClusterOffset, uint ClusterCount, uint Impostor)
{
    public const int Bytes = 12;
}

/// <summary>
/// One cluster of a model's hierarchy as the GPU reads it from the cluster pool, 80 bytes: the twin of
/// <c>Scene/Clusters.slang</c>'s. Its triangles are a run of the cluster index pool; its vertices are wherever those
/// indices point in <see cref="Vertices"/>. The cut through the hierarchy is decided per cluster, with no walk: a cluster is drawn
/// when its parent's error projects above the threshold and its own at or below it. A leaf's error is 0 and a
/// root's parent error is the model's diameter, so some level draws until the whole model is under the threshold.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuCluster
{
    public const int Bytes = 80;

    /// <summary>
    /// The most triangles a cluster holds, and the most vertices.
    /// </summary>
    public const int MaxTriangles = 128;

    /// <summary>
    /// The sphere around the cluster's triangles, in the model's frame.
    /// </summary>
    public Vector3 Center;

    public float Radius;

    /// <summary>
    /// The sphere around the group whose simplification replaces the cluster and its siblings, which holds
    /// <see cref="DetailCenter"/>'s sphere, so the parent's error projects no smaller than the cluster's.
    /// </summary>
    public Vector3 ParentCenter;

    public float ParentRadius;

    /// <summary>
    /// The sphere around the group the cluster was simplified from, the cluster's own for a leaf: what
    /// <see cref="Error"/> is projected over, the same sphere the group's children project it over as their parent's.
    /// </summary>
    public Vector3 DetailCenter;

    public float DetailRadius;

    /// <summary>
    /// How far, in metres, the cluster's surface may be from the original's: 0 for a leaf.
    /// </summary>
    public float Error;

    /// <summary>
    /// The error of the group that replaces the cluster and its siblings; the model's diameter for a root.
    /// </summary>
    public float ParentError;

    /// <summary>
    /// The first of the cluster's indices in the cluster index pool.
    /// </summary>
    public uint IndexOffset;

    public uint TriangleCount;

    /// <summary>
    /// The normal cone's axis, octahedral at 16 bits an axis, in the model's frame.
    /// </summary>
    public uint ConeAxis;

    /// <summary>
    /// The cosine of the cone's half angle, as a signed 16-bit fraction in the low half; a cluster facing every way
    /// has a cutoff of 1, which the test never rejects.
    /// </summary>
    public uint ConeCutoff;

    /// <summary>
    /// Which of the instance's material slots the cluster is drawn with.
    /// </summary>
    public uint MaterialSlot;

    /// <summary>
    /// The heap id of the pool the cluster's indices point into: the models' vertex pool for a leaf, which draws the
    /// model's own vertices, and the cluster vertex pool above, which holds each level's copies. Written as the model
    /// is copied up; the cache holds the cluster's indices into <see cref="ModelClusters.Vertices"/> with the model's
    /// own vertices before them.
    /// </summary>
    public uint Vertices;
}

/// <summary>
/// One model's impostor, 32 bytes: the twin of <c>Scene/Impostors.slang</c>'s: the box around the model in its
/// frame, and the heap id of its atlas.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuImpostor
{
    public const int Bytes = 32;

    public Vector3 BoundsMin;

    public uint Atlas;

    public Vector3 BoundsMax;

    /// <summary>
    /// The heap id of the atlas of the texture coordinates its surface texels show.
    /// </summary>
    public uint UvAtlas;
}
