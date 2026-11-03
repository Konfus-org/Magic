using System.Collections.Immutable;
using System.Numerics;
using Magic.Ecs.Components;

namespace Magic.Assets.Types;

/// <summary>
/// A piece of the world that opens as one: its chunks, evenly sized and placed by where they are, and any other prefabs. A
/// gem reads its files.
/// </summary>
public sealed class Domain : IAsset<Domain>
{
    /// <summary>
    /// Metres a chunk covers on each axis: a chunk's positions are relative to its corner.
    /// </summary>
    public const float ChunkSize = 128;

    public ImmutableArray<Chunk> Chunks { get; init; } = [];

    /// <summary>
    /// The prefabs that are no chunk, at the domain's origin.
    /// </summary>
    public ImmutableArray<Handle<Prefab>> Globals { get; init; } = [];
}

/// <summary>
/// One chunk of a <see cref="Domain"/>: which, counted in chunks along each axis, and what it holds.
/// </summary>
public readonly record struct Chunk(int X, int Y, int Z, Handle<Prefab> Prefab)
{
    /// <summary>
    /// Where its corner is.
    /// </summary>
    public Transform At => new() { Position = new Vector3(X, Y, Z) * Domain.ChunkSize };
}
