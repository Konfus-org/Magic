using Magic.Files;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Makes what a gem needs from each asset of one type, once per version of the asset. A gem registers one with
/// <see cref="AssetManager"/> for as long as it runs.
/// <see cref="AssetManager"/> runs every one a type has on what its importer made, before the load returns and again each
/// time the asset is made again, and keeps what it made in <see cref="Caches"/> under the asset's id and version, where
/// the gem reads it.
/// </summary>
public interface IAssetPreprocessor<T, TMade> : ICache<TMade>
    where T : IAsset<T>
{
    /// <summary>
    /// What the gem needs of <paramref name="asset"/>. On a worker, for several assets at once.
    /// </summary>
    TMade Preprocess(T asset);
}
