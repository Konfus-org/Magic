using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Magic.Assets;
using Magic.Assets.Types;

namespace PrefabLoader;

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
/// One entity of a file: what messages call it, its tags, its components, each by its type's name and as that type's
/// JSON, and the entities under it; when it names a prefab, that prefab's parts go under it too.
/// </summary>
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
