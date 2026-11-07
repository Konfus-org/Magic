using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Utils;

namespace Magic.Rendering;

/// <summary>
/// What the renderer draws a <see cref="Model"/> as, made from it once and cached under its version: the vertices as
/// the GPU reads them, the indices of every cluster's triangles into them, the cluster hierarchy, and the model's
/// signed distance field. A cluster's <see cref="GpuCluster.IndexOffset"/> is into <see cref="Indices"/>; the
/// renderer adds where it put them in the pools.
/// </summary>
public sealed class MeshDerived
{
    /// <summary>
    /// Voxels across <see cref="Field"/> on each axis.
    /// </summary>
    public const int FieldSize = 64;

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
    /// The signed distance to the model's surface, in a cube of <see cref="FieldSize"/> voxels a side around its
    /// bounds, each a signed byte of the distance over <see cref="FieldBand"/>, negative inside: what a ray through
    /// the world is traced against.
    /// </summary>
    public required ImmutableArray<byte> Field { get; init; }

    /// <summary>
    /// The first voxel's centre, in the model's frame.
    /// </summary>
    public required Vector3 FieldOrigin { get; init; }

    /// <summary>
    /// Metres between voxels.
    /// </summary>
    public required float FieldVoxel { get; init; }

    /// <summary>
    /// Metres a field value of 1 means: the farthest the field tells from a surface.
    /// </summary>
    public required float FieldBand { get; init; }
}

/// <summary>
/// Makes <see cref="MeshDerived"/> from a model, as a gem provides it: reads the model, makes what the cache lacks
/// and keeps it there. Any thread; the work runs on a worker.
/// </summary>
public interface IMeshPreprocessor
{
    Task<Result<MeshDerived>> GetAsync(
        Handle<Model> model,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    );
}
