namespace Magic.Attributes;

public enum AssetFormat
{
    /// <summary>
    /// The file is the asset class as JSON (see <see cref="Contexts.Assets.AssetJson"/>).
    /// </summary>
    Json,

    /// <summary>
    /// The file is text; it goes into the class's <c>public string Text { get; set; }</c>.
    /// </summary>
    Text,

    /// <summary>
    /// The file is taken as it is; its bytes go into the class's <c>public byte[] Data { get; set; }</c>, for a type
    /// whose own code reads them when and how it needs to.
    /// </summary>
    Binary,

    /// <summary>
    /// The file is read by the <see cref="Interfaces.IAssetLoader{T}"/> a gem exports for the type.
    /// </summary>
    Custom
}

/// <summary>
/// Says how the host reads an asset type's file. Without this attribute the format is <see cref="AssetFormat.Custom"/>,
/// so a gem must export an <see cref="Interfaces.IAssetLoader{T}"/> for the type. Either way the <c>.meta</c> sidecar
/// is deserialised as the asset type first.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetFormatAttribute(AssetFormat format) : Attribute
{
    public AssetFormat Format { get; } = format;
}
