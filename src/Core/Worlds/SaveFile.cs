using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Ecs;

namespace Magic.Worlds;

/// <summary>
/// A game save as its file holds it, and the world that a restart keeps: the open domains, and the changes to them.
/// </summary>
/// <remarks>
/// The changes are a JSON diff from the <see cref="WorldState"/> that the files of the domains make to that of the
/// world. They are null when the world does not differ.
/// </remarks>
internal sealed record SaveFile(ImmutableArray<Handle<Domain>> Domains, JsonNode? Changes);

/// <summary>
/// The world as plain data: its entities with their components by type name, and the pause and the time scale.
/// </summary>
/// <remarks>
/// The entities that a domain placed come first, by their key, which is the same in each run. A restart keeps the
/// state only as JSON, which names types and holds no instances of them, so code can load again under it.
/// </remarks>
internal sealed record WorldState(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IComponent>> Placed,
    ImmutableArray<IReadOnlyDictionary<string, IComponent>> Made,
    bool IsPaused,
    float TimeScale
);
