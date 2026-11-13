using System.Collections.Immutable;
using Magic.Files;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Reads the files of one asset type, from a file format on disk into what the engine works with.
/// </summary>
/// <remarks>
/// A gem gives one for its format as an exported part that implements it. The host gives it to
/// <see cref="AssetManager"/> when the gem is made, and removes it when the gem ends. One importer reads a type. Core
/// reads a type marked <see cref="AssetFormatAttribute"/> itself. When an importer is also an <see cref="ICache{T}"/>,
/// Core caches what it imports by the asset's id and version, and does not call it again until the file or its
/// <c>.meta</c> changes.
/// </remarks>
public interface IAssetImporter<T>
    where T : IAsset<T>
{
    /// <summary>
    /// The file extensions it reads, matched without case against the end of a file name, so <c>.png</c> also matches
    /// <c>.normal.png</c>.
    /// </summary>
    /// <remarks>
    /// A file of the type with another extension is refused before it is read.
    /// </remarks>
    ImmutableArray<string> Extensions { get; }

    /// <summary>
    /// Makes the asset that <paramref name="file"/> holds, or tells why it is not one.
    /// </summary>
    /// <remarks>
    /// Load what it is made from, such as the textures of a font's atlas or the prefabs that a prefab places, with
    /// <see cref="AssetManager.LoadAsync{T}(Handle{T}, ulong, IProgress{LoadProgress}?, CancellationToken)"/>. Name the
    /// id of <paramref name="file"/> as the dependent, so the asset is made again when one of them changes. It runs on
    /// a worker thread, for several files at the same time.
    /// </remarks>
    Task<Result<T>> ImportAsync(AssetFile file, IProgress<LoadProgress>? progress, CancellationToken cancel);
}
