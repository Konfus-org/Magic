using System.Collections.Immutable;
using Magic.Assets;
using Magic.Ecs;

namespace Magic.Worlds;

/// <summary>
/// The world as data (<see cref="World.SaveAsync"/>), which <see cref="World.RestoreAsync(WorldState, IProgress{Utils.LoadProgress}, CancellationToken)"/>
/// puts back. An asset, so a saved world is a file a gem reads and writes (<see cref="IWorldStateWriter"/>).
/// </summary>
public sealed record WorldState : IAsset<WorldState>
{
    public required ImmutableArray<WorldContent> Contents { get; init; }

    public required ImmutableArray<EntityTemplate> Entities { get; init; }

    public bool IsPaused { get; init; }

    public float TimeScale { get; init; } = 1;
}
