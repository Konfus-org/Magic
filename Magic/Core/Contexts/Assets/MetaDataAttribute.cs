namespace Magic.Attributes.Assets;

/// <summary>
/// Marks an asset property whose value lives in the asset's <c>.meta</c> sidecar rather than in the asset
/// file itself: import configuration such as a texture's wrap mode or a material's blend type. The sidecar is
/// deserialised as the asset type, so a loader simply reads the property. For <see cref="AssetFormatAttribute"/>
/// JSON assets, where the file is deserialised as the same type, the manager copies these properties from the
/// sidecar instance onto the file instance. The sidecar key is the camelCase property name.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MetaDataAttribute : Attribute
{
}
