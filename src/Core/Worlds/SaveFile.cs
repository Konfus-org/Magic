using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Ecs;

namespace Magic.Worlds;

/// <summary>
/// A game save as its file holds it, and the world a restart keeps: the domains open, and how the world differs from
/// their files, a JSON diff of the <see cref="WorldState"/> they make to the world's; none when it doesn't.
/// </summary>
internal sealed record SaveFile(ImmutableArray<Handle<Domain>> Domains, JsonNode? Changes);

/// <summary>
/// The world as plain data: each entity a domain placed by its key, the same in every run, then every other entity, each
/// with its components by their type's name; and the pause and time scale. A restart keeps it only as JSON, which names
/// types and holds no instances of them, so code can reload under it.
/// </summary>
internal sealed record WorldState(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IComponent>> Placed,
    ImmutableArray<IReadOnlyDictionary<string, IComponent>> Made,
    bool IsPaused,
    float TimeScale
);
