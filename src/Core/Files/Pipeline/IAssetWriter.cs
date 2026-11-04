using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Writes the files of one asset type: an exporter, turning what the engine works with into its file format on disk,
/// as <see cref="IAssetLoader{T}"/> turns the format back. A gem provides one for its format by implementing it; the
/// host hands it to <see cref="Assets"/> as the gem is made, and takes it back as the gem ends. One writer
/// writes a type. The writer makes the bytes alone: the database puts them on disk with the sidecar that makes them an
/// asset (<see cref="Assets.WriteAsync{T}"/>).
/// </summary>
public interface IAssetWriter<T>
    where T : IAsset<T>
{
    /// <summary>
    /// The file extension it writes, such as <c>.prefab</c>, which a file written must end in.
    /// </summary>
    string Extension { get; }

    /// <summary>
    /// The file that holds <paramref name="asset"/>, or why it can't be written. On a worker.
    /// </summary>
    Task<Result<ReadOnlyMemory<byte>>> WriteAsync(T asset, IProgress<LoadProgress>? progress, CancellationToken cancel);
}
