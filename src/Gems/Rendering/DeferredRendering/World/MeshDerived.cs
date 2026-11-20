using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;

namespace Magic.DeferredRendering.World;

/// <summary>
/// What the GPU holds a <see cref="Model"/> as, made from it once and cached under its version.
/// </summary>
/// <remarks>
/// It holds its meshes' vertices as the GPU reads them, one mesh after another, and their triangles as indices into
/// them, which rays and every renderer draw. The renderer adds where it put them in the pools.
/// </remarks>
internal sealed class MeshDerived
{
    internal required ImmutableArray<GpuVertex> Vertices { get; init; }

    internal required ImmutableArray<uint> Indices { get; init; }

    /// <summary>
    /// Each of the model's meshes in order: where its triangles start in <see cref="Indices"/>, and its material slot.
    /// </summary>
    internal required ImmutableArray<GpuMesh> Meshes { get; init; }

    /// <summary>
    /// The sphere around the whole model, in its frame.
    /// </summary>
    internal required Vector3 BoundsCenter { get; init; }

    internal required float BoundsRadius { get; init; }

    /// <summary>
    /// The box around the whole model, in its frame.
    /// </summary>
    internal required Vector3 BoundsMin { get; init; }

    internal required Vector3 BoundsMax { get; init; }
}
