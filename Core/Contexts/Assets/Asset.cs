using System.Text.Json.Serialization;

namespace Magic.Contexts.Assets;

/// <summary>
/// Something loaded from a file under the Resources or Assets root. Its <c>.meta</c> sidecar is this class as
/// JSON: <see cref="Id"/>, <see cref="Version"/> and whatever import settings the type keeps there (see
/// <see cref="Attributes.MetaDataAttribute"/>). The manager deserialises the sidecar as the asset type, then a
/// loader (or the built-in JSON/text reading, see <see cref="Attributes.AssetFormatAttribute"/>) fills in the rest.
/// An asset is not modified after it is published.
/// </summary>
public abstract class Asset
{
    /// <summary>The id from the sidecar; what a <see cref="Handle{T}"/> carries.</summary>
    public ulong Id { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>Relative to the root it was found under (Resources or Assets), forward slashes.</summary>
    [JsonIgnore]
    public string Path { get; set; } = "";

    [JsonIgnore]
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}
