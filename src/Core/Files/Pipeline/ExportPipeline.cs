using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Writes one <typeparamref name="T"/> as an asset, a step at a time: its exporter makes the bytes, they go on disk, and
/// the sidecar goes beside them, under an id the path decides.
/// </summary>
internal static class ExportPipeline<T>
    where T : IAsset<T>
{
    public static async Task<Result<Handle<T>>> RunAsync(
        T asset,
        string path,
        IAssetExporter<T> exporter,
        string directory,
        IFileSystem files,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (!path.EndsWith(exporter.Extension, StringComparison.OrdinalIgnoreCase))
            return Result.Failure($"a {typeof(T).Name}'s file ends in {exporter.Extension}, not {path}.");

        Result<ReadOnlyMemory<byte>> bytes = await exporter.ExportAsync(asset, progress, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        string file = files.Combine(directory, path);
        Result written = await files.WriteAsync(file, bytes.Value, cancel).ConfigureAwait(false);
        if (!written.Ok)
            return Result.Failure(written.Error);

        ulong id = GetId(path);
        Result sidecar = await WriteSidecarAsync(id, file, files, cancel).ConfigureAwait(false);

        return sidecar.Ok ? Result.Success(new Handle<T>(id)) : Result.Failure(sidecar.Error);
    }

    /// <summary>
    /// The id of an asset written at the asset path <paramref name="path"/>: the same for it each time, whatever the
    /// case, and never none.
    /// </summary>
    private static ulong GetId(string path) =>
        Math.Max(XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(path.ToUpperInvariant())), 1);

    private static async Task<Result> WriteSidecarAsync(
        ulong id,
        string file,
        IFileSystem files,
        CancellationToken cancel
    )
    {
        Result<JsonElement> sidecar = Json.Write(
            new Dictionary<string, object> { ["id"] = id, ["type"] = typeof(T).Name },
            typeof(Dictionary<string, object>)
        );
        if (!sidecar.Ok)
            return Result.Failure(sidecar.Error);

        return await files
            .WriteAsync(file + AssetIndex.Sidecar, Encoding.UTF8.GetBytes(sidecar.Value.GetRawText()), cancel)
            .ConfigureAwait(false);
    }
}
