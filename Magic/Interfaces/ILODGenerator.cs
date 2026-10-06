using Magic.Contexts.Assets;
using Magic.Utils;

namespace Magic.Interfaces;

/// <summary>
/// Makes the lesser versions of an asset that has none of its own (<see cref="Asset.Lods"/>): cheaper stand-ins of
/// the same type, each good from some threshold on. A gem provides one by implementing it; the asset manager asks the
/// container for the <see cref="ILODGenerator{T}"/> of the type, the first time the LODs of an asset are wanted, and
/// keeps what it made in the cache until the asset's file, its sidecar or <see cref="Version"/> changes, or any
/// other asset the generator loaded through the asset manager while it made them does.
/// </summary>
public interface ILODGenerator<T> where T : Asset
{
    /// <summary>
    /// Raised whenever the generator would make something else of the same asset, so what older ones made is made again.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Writes the lesser versions of <paramref name="asset"/> as asset files directly in <paramref name="folder"/>,
    /// which is this asset's alone, and answers them (<see cref="Lod"/>: each one's threshold, its file's id and the
    /// textures baked for it). A file in the folder has no sidecar: its id is <see cref="Lods.IdOf"/> of its name,
    /// which is also how a file names another (a chunk its stand-in models). An asset not worth a lesser version is
    /// <see cref="Lods.None"/>. Called on a worker. It tells <paramref name="progress"/>, when there is
    /// one, how much of the work is done, 0 to 1. It stops, by throwing
    /// <see cref="OperationCanceledException"/>, when <paramref name="cancel"/> is cancelled; what it had written by
    /// then is made again the next time.
    /// </summary>
    Task<Result<Lods>> GenerateAsync(T asset, string folder, IProgress<float>? progress, CancellationToken cancel);
}
