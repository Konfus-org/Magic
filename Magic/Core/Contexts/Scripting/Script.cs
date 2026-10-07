using Magic.Attributes.Assets;
using Magic.Interfaces;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// A script file as an asset, whatever language it is written in: what the language's gem (its <see cref="IAssetLoader{T}"/>
/// of scripts) says about the script's class once loaded, which is all the script system runs it by. Entities name it in
/// a chunk by id.
/// </summary>
[AssetFormat(AssetFormat.Custom)]
public sealed class Script : Asset
{
    /// <summary>
    /// Updated every frame however far it is from every camera: for what must see every frame, not what only moves.
    /// </summary>
    [JsonIgnore]
    public bool AlwaysUpdate { get; set; }

    /// <summary>
    /// Made and run as its entity spawns even while the world is loading, when the others wait for it: the loading
    /// screen's own scripts.
    /// </summary>
    [JsonIgnore]
    public bool Impatient { get; set; }

    /// <summary>
    /// One instance for an entity, from its chunk entry (<c>{ "id": N, ... }</c>, the rest its values); null, or a
    /// throw, when it cannot be made. Null until the script is loaded.
    /// </summary>
    [JsonIgnore]
    public Func<Handle, Unread, IScript?>? Make { get; set; }
}
