namespace Magic.Assets.Pipeline;

/// <summary>
/// An asset's file as its <see cref="IAssetLoader{T}"/> is handed it.
/// </summary>
/// <param name="Id">The asset's id, which its dependencies are loaded for.</param>
/// <param name="Path">Where it is from its asset directory, with <c>/</c>: for its extension and for messages.</param>
/// <param name="Bytes">What the file holds.</param>
/// <param name="Metadata">
/// Its import settings: the values of the asset type's <see cref="AssetMetaAttribute"/> properties, as its
/// <c>.meta</c> sets them, which the loader copies onto what it makes.
/// </param>
/// <param name="Version">
/// A hash of the file and its <c>.meta</c>, which changes when either does: what a cache of work done on it is
/// checked against.
/// </param>
public readonly record struct AssetFile(
    ulong Id,
    string Path,
    ReadOnlyMemory<byte> Bytes,
    AssetMetadata Metadata,
    ulong Version
);
