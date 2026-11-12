using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Magic.Assets;

namespace Magic.PrefabImporter;

/// <summary>
/// A <c>.prefab</c> file as it is written, <c>{ "entities": [ ... ] }</c>.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it as it reads a file."
)]
internal sealed class PrefabFile
{
    public ImmutableArray<PrefabFileEntity> Entities { get; set; } = [];
}

/// <summary>
/// One entity of a file: its name in messages, its tags, its components, and the entities under it.
/// </summary>
/// <remarks>
/// Each component is its type's name and that type's JSON. When the entity names a prefab, the prefab's parts go under
/// it too.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The serializer makes it as it reads a file."
)]
internal sealed class PrefabFileEntity
{
    public string Name { get; set; } = string.Empty;

    public Handle<Prefab> Prefab { get; set; }

    public ImmutableArray<string> Tags { get; set; } = [];

    public IReadOnlyDictionary<string, JsonElement> Components { get; set; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public ImmutableArray<PrefabFileEntity> Children { get; set; } = [];
}
