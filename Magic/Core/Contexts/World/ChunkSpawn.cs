using Magic.Contexts.Assets;
using System.Numerics;

namespace Magic.Contexts.Domain;

/// <summary>
/// <see cref="Magic.Services.World.Spawn"/> asked for <see cref="Chunk"/>'s entities under a root at <see cref="At"/>,
/// outside any domain: whatever fills the world spawns them.
/// </summary>
public readonly record struct ChunkSpawn(Handle<Chunk> Chunk, Vector3 At);
