using System.Collections.Immutable;
using Magic.Ecs;
using Magic.Files;
using Magic.Files.Types;

namespace Magic.Worlds;

/// <summary>
/// The whole world as it is, which a restart keeps and puts back: the domains open, each entity they placed by its key, the
/// keys of those destroyed, every other entity, and its pause and time scale.
/// </summary>
internal sealed class WorldState
{
    public required ImmutableArray<Handle<Domain>> Domains { get; init; }

    public required ImmutableArray<Placement> Placed { get; init; }

    public required ImmutableArray<EntityKey> Destroyed { get; init; }

    /// <summary>
    /// The entities no domain placed: globals, the player, and those made as the game ran.
    /// </summary>
    public required ImmutableArray<ImmutableArray<IComponent>> Entities { get; init; }

    public required bool IsPaused { get; init; }

    public required float TimeScale { get; init; }
}

/// <summary>
/// A game save as its file holds it: the domains open, what changed against their files, and the pause and time scale.
/// </summary>
internal sealed record SaveFile(
    ImmutableArray<Handle<Domain>> Domains,
    WorldChanges Changes,
    bool IsPaused,
    float TimeScale
);
