using Magic.Assets.Pipeline;

namespace Magic.Assets;

/// <summary>
/// Marks an asset property as an import setting that the <c>.meta</c> sets under its camelCase name, such as a
/// texture's <c>"wrap"</c>.
/// </summary>
/// <remarks>
/// The default is the property's initializer. A <c>.meta</c> key that matches no marked property is an error. Core
/// gives the values to the type's <see cref="IAssetImporter{T}"/> as <see cref="AssetFile.Metadata"/>, and the
/// importer copies them onto what it makes. For a type that Core reads itself, Core sets them.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class AssetMetaAttribute : Attribute;
