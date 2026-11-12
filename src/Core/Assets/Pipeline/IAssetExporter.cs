using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Writes the files of one asset type, from what the engine works with into its file format on disk.
/// </summary>
/// <remarks>
/// A gem gives one for its format as an exported part that implements it. The host gives it to
/// <see cref="AssetManager"/> when the gem is made, and removes it when the gem ends. One exporter writes a type. It
/// makes only the bytes: Core puts them on disk (<see cref="AssetManager.ExportAsync{T}"/>).
/// </remarks>
public interface IAssetExporter<T>
    where T : IAsset<T>
{
    /// <summary>
    /// The file extension it writes, such as <c>.prefab</c>, which a file written must end in.
    /// </summary>
    string Extension { get; }

    /// <summary>
    /// Makes the file that holds <paramref name="asset"/>, or tells why it cannot.
    /// </summary>
    /// <remarks>
    /// It runs on a worker thread.
    /// </remarks>
    Task<Result<ReadOnlyMemory<byte>>> ExportAsync(
        T asset,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    );
}
