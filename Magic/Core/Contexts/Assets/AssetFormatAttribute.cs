namespace Magic.Attributes.Assets;

/// <summary>
/// How an asset type's file is read, as its <see cref="AssetFormatAttribute"/> says.
/// </summary>
public enum AssetFormat
{
    /// <summary>
    /// The file is the asset class as JSON (see <see cref="Services.AssetJson"/>).
    /// </summary>
    Json,

    /// <summary>
    /// The file is read by the <see cref="Interfaces.IAssetLoader{T}"/> a gem exports for the type.
    /// </summary>
    Custom
}

/// <summary>
/// Says how <see cref="Services.Assets"/> reads an asset type's file. A <see cref="Contexts.Assets.TextAsset"/> or a
/// <see cref="Contexts.Assets.BinaryAsset"/> needs none: its file is its text or its bytes. Without this attribute the
/// format is <see cref="AssetFormat.Custom"/>, so a gem must export an <see cref="Interfaces.IAssetLoader{T}"/> for the
/// type. Either way the <c>.meta</c> sidecar is deserialised as the asset type first.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetFormatAttribute(AssetFormat format) : Attribute
{
    public AssetFormat Format { get; } = format;

    /// <summary>
    /// File extensions (<c>".rtex"</c>) only this type reads: a handle of another type that names such a file is not
    /// one to load as that type (a texture parameter naming a render texture).
    /// </summary>
    public string[] Extensions { get; init; } = [];
}
