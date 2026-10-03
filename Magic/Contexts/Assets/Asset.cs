using Magic.Attributes;
using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// Something loaded from a file under the Resources or Assets root. Its <c>.meta</c> sidecar is this class as
/// JSON: <see cref="Id"/>, <see cref="Version"/> and whatever import settings the type keeps there (see
/// <see cref="Attributes.MetaDataAttribute"/>). The manager deserialises the sidecar as the asset type, then a
/// loader (or the built-in JSON/text reading, see <see cref="Attributes.AssetFormatAttribute"/>) fills in the rest.
/// An asset is not modified after it is loaded: the manager pools it, so every Load of it gets the same object.
/// </summary>
public abstract class Asset
{
    /// <summary>
    /// The id from the sidecar; what a <see cref="Handle{T}"/> carries.
    /// </summary>
    public ulong Id { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>
    /// Relative to the root it was found under (Resources or Assets), forward slashes.
    /// </summary>
    [JsonIgnore]
    public string Path { get; set; } = "";

    [JsonIgnore]
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// The folder <see cref="Path"/> is in, in the same form (forward slashes, no trailing one); empty at the root.
    /// </summary>
    [JsonIgnore]
    public string Folder => Path[..Math.Max(0, Path.LastIndexOf('/'))];

    /// <summary>
    /// Lesser versions of this asset, cheaper stand-ins of the same type: the id of each by its threshold (for a
    /// model the height on screen, as a fraction of the view's, under which it is drawn; for a chunk the metres from
    /// a camera beyond which it is spawned). Written in the sidecar by whoever authored them; an asset with none
    /// gets the ones an <see cref="Interfaces.ILODGenerator{T}"/> makes, when a gem provides one for its type.
    /// </summary>
    [MetaData]
    public Dictionary<float, ulong> Lods { get; set; } = [];

    /// <summary>
    /// Roughly what the loaded asset holds in memory, for the pool's budgets; 0 means about its file's size. Types whose
    /// loaded form is far bigger than their file (decoded pixels, imported meshes) say so.
    /// </summary>
    [JsonIgnore]
    public virtual long Bytes => 0;
}
