using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Writes the files of one asset type, turning what the engine works with into its file format on disk, as
/// <see cref="IAssetImporter{T}"/> turns the format back. A gem provides one for its format by implementing it; the host
/// hands it to <see cref="Assets"/> as the gem is made, and takes it back as the gem ends. One exporter writes a type. It
/// makes the bytes alone: Core puts them on disk with the sidecar that makes them an asset
/// (<see cref="Assets.SaveAsync{T}"/>).
/// </summary>
public interface IAssetExporter<T>
    where T : IAsset<T>
{
    /// <summary>
    /// The file extension it writes, such as <c>.prefab</c>, which a file written must end in.
    /// </summary>
    string Extension { get; }

    /// <summary>
    /// The file that holds <paramref name="asset"/>, or why it can't be written. On a worker.
    /// </summary>
    Task<Result<ReadOnlyMemory<byte>>> ExportAsync(
        T asset,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    );
}
