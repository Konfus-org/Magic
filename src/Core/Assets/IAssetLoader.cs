using System.Collections.Immutable;
using Magic.Utils;

namespace Magic.Assets;

/// <summary>
/// Reads the files of one asset type: an importer, turning a file format on disk into what the engine works with. A
/// gem provides one for its format by implementing it; the host hands it to <see cref="AssetManager"/> as the gem is
/// made, and takes it back as the gem ends. One loader reads a type. A type marked <see cref="AssetFormatAttribute"/> is read
/// by Core itself.
/// </summary>
public interface IAssetLoader<T>
    where T : IAsset<T>
{
    /// <summary>
    /// The file extensions it reads, matched ignoring case against the end of a file's name, so <c>.png</c> is a
    /// <c>.normal.png</c>'s too. A file of the type with another is refused before it is read.
    /// </summary>
    ImmutableArray<string> Extensions { get; }

    /// <summary>
    /// The asset <paramref name="file"/> holds, or why it is not one. What it is made from, such as the textures a
    /// font's atlas or the prefabs a prefab places, it loads with
    /// <see cref="AssetManager.LoadAsync{T}(Handle{T}, ulong, IProgress{LoadProgress}?, CancellationToken)"/>, naming
    /// <paramref name="file"/>'s id as the dependent, so the asset is made again when one of them changes. On a worker,
    /// for several files at once.
    /// </summary>
    Task<Result<T>> LoadAsync(AssetFile file, IProgress<LoadProgress>? progress, CancellationToken cancel);
}
