using System.Collections.Immutable;
using Magic.Assets;
using Magic.Ecs;

namespace Magic.Worlds;

/// <summary>
/// A game save as the world takes it and puts it back, and as the save serializer writes and reads it: the open domains,
/// and the changes to them.
/// </summary>
/// <remarks>
/// The changes are what the world differs by from the <see cref="WorldState"/> that the files of the domains make. The
/// components in them are values that the world copied, so a writer must finish before the next save frees the script
/// state that those values refer to.
/// </remarks>
public sealed record SaveData(ImmutableArray<Handle<Domain>> Domains, WorldChanges Changes);

/// <summary>
/// The world as plain data: its entities with their components by type name, and the pause and the time scale.
/// </summary>
/// <remarks>
/// The entities that a domain placed come first, by their key, which is the same in each run.
/// </remarks>
internal sealed record WorldState(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IComponent>> Placed,
    ImmutableArray<IReadOnlyDictionary<string, IComponent>> Made,
    bool IsPaused,
    float TimeScale
);
