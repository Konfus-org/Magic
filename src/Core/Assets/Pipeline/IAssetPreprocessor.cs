using Magic.Files;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Makes what a gem needs from each asset of one type, once per version of the asset.
/// </summary>
/// <remarks>
/// A gem registers one with <see cref="AssetManager"/> for as long as it runs. <see cref="AssetManager"/> runs each
/// preprocessor of a type on what the importer made, before the load returns and again each time the asset is made
/// again. It keeps the result in <see cref="Caches"/> under the asset's id and version, where the gem reads it.
/// </remarks>
public interface IAssetPreprocessor<T, TMade> : ICache<TMade>
    where T : IAsset<T>
{
    /// <summary>
    /// Makes what the gem needs from <paramref name="asset"/>.
    /// </summary>
    /// <remarks>
    /// It runs on a worker thread, for several assets at the same time.
    /// </remarks>
    TMade Preprocess(T asset);
}
