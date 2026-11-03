using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Assets.Types;
using Magic.Utils;

namespace Magic.Rendering;

/// <summary>
/// What the renderer draws a <see cref="Model"/> as, made from it once and cached under its version: the vertices as
/// the GPU reads them, the indices of every cluster's triangles into them, and the cluster hierarchy. A cluster's
/// <see cref="GpuCluster.IndexOffset"/> is into <see cref="Indices"/>; the renderer adds where it put them in the pools.
/// </summary>
public sealed class MeshDerived
{
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
