using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;

namespace Magic.GpuWorld;

/// <summary>
/// What the renderer draws a <see cref="Model"/> as, made from it once and cached under its version: the vertices as
/// the GPU reads them, the indices of every cluster's triangles into them, the cluster hierarchy, and the model's
/// impostor. A cluster's <see cref="GpuCluster.IndexOffset"/> is into <see cref="Indices"/>; the renderer adds where
/// it put them in the pools.
/// </summary>
public sealed class MeshDerived
{
    /// <summary>
    /// Views across and down an impostor atlas, on an octahedral grid over the whole sphere.
    /// </summary>
    public const int ImpostorViews = 12;

    /// <summary>
    /// Texels a side of one view of an impostor atlas.
    /// </summary>
    public const int ImpostorTile = 64;

    public const int ImpostorSize = ImpostorViews * ImpostorTile;

    public required ImmutableArray<GpuVertex> Vertices { get; init; }

    public required ImmutableArray<uint> Indices { get; init; }

    /// <summary>
    /// Every cluster of every level, leaves first.
    /// </summary>
    public required ImmutableArray<GpuCluster> Clusters { get; init; }

    /// <summary>
    /// The sphere around the whole model, in its frame.
    /// </summary>
    public required Vector3 BoundsCenter { get; init; }

    public required float BoundsRadius { get; init; }

    /// <summary>
    /// The model's octahedral impostor atlas, <see cref="ImpostorViews"/> by <see cref="ImpostorViews"/> views of
    /// <see cref="ImpostorTile"/> texels a side, row by row as RGBA16F: each texel the octahedral normal, the depth from
    /// the view's front plane over the sphere's diameter, and the material slot plus one, 0 where the view sees nothing.
    /// Empty for a model too big to stand far away as one.
    /// </summary>
    public required ImmutableArray<Half> Impostor { get; init; }

    /// <summary>
    /// The texture coordinates of the surface each texel of <see cref="Impostor"/> shows, at the same texels, as
    /// RG16F; empty with it.
    /// </summary>
    public required ImmutableArray<Half> ImpostorUv { get; init; }

    /// <summary>
    /// The box around the whole model, in its frame: what an impostor's quad is fitted to.
    /// </summary>
    public required Vector3 BoundsMin { get; init; }

    public required Vector3 BoundsMax { get; init; }
}
