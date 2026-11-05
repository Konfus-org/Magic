using System.Collections.Immutable;
using System.Linq;
using Magic.Ecs;

namespace Magic.Files.Types;

/// <summary>
/// A world as it was, which it can be put back as: <see cref="Worlds.World.SaveAsync"/> gives one and
/// <see cref="Worlds.World.RestoreAsync"/> takes one. Save it with <see cref="Assets.SaveAsync{T}"/> as a <c>.world</c> file
/// and load it as any asset. Its file is it as JSON, each component naming its type.
/// </summary>
[AssetFormat(AssetFileFormat.Json)]
public sealed class WorldState : IAsset<WorldState>
{
    /// <summary>
    /// The domains open, each with the entities it put there.
    /// </summary>
    public required ImmutableArray<DomainState> Domains { get; init; }

    /// <summary>
    /// The entities no domain put there: globals, the player, and those made as the game ran.
    /// </summary>
    public required ImmutableArray<ImmutableArray<IComponent>> Entities { get; init; }

    public required bool IsPaused { get; init; }

    public required float TimeScale { get; init; }

    internal int EntityCount => Entities.Length + Domains.Sum(domain => domain.Entities.Length);
}

/// <summary>
/// A domain open in a <see cref="WorldState"/>, and the entities it put there, each as its components.
/// </summary>
public sealed class DomainState
{
    public required Handle<Domain> Domain { get; init; }

    public required ImmutableArray<ImmutableArray<IComponent>> Entities { get; init; }
}
