using Magic.Assets.Pipeline;

namespace Magic.Assets;

/// <summary>
/// Marks a property of an asset type as set by its <c>.meta</c>, under its name in camelCase: an import setting, such
/// as a texture's <c>"wrap"</c>. Its default is its initializer. Core reads the <c>.meta</c> against the marked
/// properties, a key that is none of them an error, and hands their values to the type's
/// <see cref="IAssetLoader{T}"/> as <see cref="AssetFile.Metadata"/>, which the loader copies onto what it makes. For a
/// type Core reads itself, Core sets them.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class AssetMetaAttribute : Attribute;
