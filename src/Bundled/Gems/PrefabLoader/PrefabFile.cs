using System.Text.Json;
using Magic.Contexts.Assets;

namespace Magic.PrefabLoader;

/// <summary>
/// A <c>.prefab</c> file as it is written, <c>{ "entities": [ ... ] }</c>, which is read into a
/// <see cref="Prefab"/>. Only the loader sees it.
/// </summary>
internal sealed class PrefabFile
{
    public IReadOnlyList<PrefabFileEntity> Entities { get; set; } = [];
}

/// <summary>
/// One entity of a <see cref="PrefabFile"/>: what messages call it, its tags, its components, each by its type's name
/// and as that type's JSON, its scripts, each by the id of its <see cref="Script"/> with its values beside, the
/// entities under it; and when it names a <see cref="Prefab"/>, that prefab's parts under it too.
/// </summary>
internal sealed class PrefabFileEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A prefab it places: its parts go under this one, placed by its transform.
    /// </summary>
    public Handle<Prefab> Prefab { get; set; }

    public IReadOnlyList<string> Tags { get; set; } = [];

    public IReadOnlyDictionary<string, JsonElement> Components { get; set; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>
    /// Each <c>{ "id": N, ... }</c>: a script by its id, and every other key a property of the script it sets.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Scripts { get; set; } = [];

    /// <summary>
    /// The entities under it, their transforms relative to its own.
    /// </summary>
    public IReadOnlyList<PrefabFileEntity> Children { get; set; } = [];
}
