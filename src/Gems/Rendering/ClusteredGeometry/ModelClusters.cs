using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.DeferredRendering.World;

namespace Magic.ClusteredGeometry;

/// <summary>
/// What the cluster renderer draws a <see cref="Model"/> as, made from it once and cached under its version.
/// </summary>
/// <remarks>
/// It holds the cluster hierarchy, the indices of every cluster's triangles, the vertices that the levels above the
/// leaves draw, and the model's impostor. An index under <see cref="ModelVertexCount"/> is a vertex of the model's own,
/// as the renderer holds them; one at or over it is <see cref="Vertices"/>' at the index less that count. A cluster's
/// <see cref="GpuCluster.IndexOffset"/> is into <see cref="Indices"/>; the renderer adds where it put them in the
/// pools.
/// </remarks>
internal sealed class ModelClusters
{
    /// <summary>
    /// How many vertices the model has of its own, every mesh's one after another: what a leaf draws.
    /// </summary>
    internal required int ModelVertexCount { get; init; }

    /// <summary>
    /// The copies of the model's vertices that each level above the leaves draws, with normals of its own triangles.
    /// </summary>
    internal required ImmutableArray<GpuVertex> Vertices { get; init; }

    internal required ImmutableArray<uint> Indices { get; init; }

    /// <summary>
    /// Every cluster of every level, each mesh's leaves first.
    /// </summary>
    internal required ImmutableArray<GpuCluster> Clusters { get; init; }

    /// <summary>
    /// The model's octahedral impostor atlas, row by row as RGBA16F.
    /// </summary>
    /// <remarks>
    /// It has <see cref="ImpostorBuilder.Views"/> by <see cref="ImpostorBuilder.Views"/> views of
    /// <see cref="ImpostorBuilder.Tile"/> texels a side. Each texel holds the octahedral normal, the depth from the
    /// view's front plane over the sphere's diameter, and the material slot plus one, 0 where the view sees nothing.
    /// </remarks>
    internal required ImmutableArray<Half> Impostor { get; init; }

    /// <summary>
    /// The texture coordinates of the surface each texel of <see cref="Impostor"/> shows, at the same texels, as
    /// RG16F.
    /// </summary>
    internal required ImmutableArray<Half> ImpostorUv { get; init; }

    /// <summary>
    /// The texels that each view of <see cref="Impostor"/> saw something at, view by view, row by row.
    /// </summary>
    /// <remarks>
    /// A view's value holds its first column and row in the low two bytes, and one past its last column and row in the
    /// high two. It is 0 for a view that saw nothing.
    /// </remarks>
    internal required ImmutableArray<uint> ImpostorViewBounds { get; init; }

    /// <summary>
    /// The box around the whole model, in its frame: the outline that an impostor's quad stays inside.
    /// </summary>
    internal required Vector3 BoundsMin { get; init; }

    internal required Vector3 BoundsMax { get; init; }
}
