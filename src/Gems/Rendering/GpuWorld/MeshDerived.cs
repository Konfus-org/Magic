using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;

namespace Magic.GpuWorld;

/// <summary>
/// What the GPU holds a <see cref="Model"/> as, made from it once and cached under its version: its meshes' vertices
/// as the GPU reads them, one mesh after another, and their triangles as indices into them, which rays and every
/// renderer draw. The renderer adds where it put them in the pools.
/// </summary>
public sealed class MeshDerived
{
    public required ImmutableArray<GpuVertex> Vertices { get; init; }

    public required ImmutableArray<uint> Indices { get; init; }

    /// <summary>
    /// Each of the model's meshes in order: where its triangles start in <see cref="Indices"/>, and its material slot.
    /// </summary>
    public required ImmutableArray<GpuMesh> Meshes { get; init; }

    /// <summary>
    /// The sphere around the whole model, in its frame.
    /// </summary>
    public required Vector3 BoundsCenter { get; init; }

    public required float BoundsRadius { get; init; }

    /// <summary>
    /// The box around the whole model, in its frame.
    /// </summary>
    public required Vector3 BoundsMin { get; init; }

    public required Vector3 BoundsMax { get; init; }
}
