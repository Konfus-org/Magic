using System.Text.Json;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// One <typeparamref name="T"/> being loaded (<see cref="ImportPipeline{T}"/>): the asset's file as far as it has been
/// read, and what was made of it.
/// </summary>
internal readonly record struct AssetLoad<T>
    where T : IAsset<T>
{
    public required ulong Id { get; init; }

    /// <summary>
    /// Where it is from its asset directory, with <c>/</c>.
    /// </summary>
    public required string Path { get; init; }

    public ReadOnlyMemory<byte> Bytes { get; init; }

    /// <summary>
    /// Its import settings, as its <c>.meta</c> sets them.
    /// </summary>
    public AssetMetadata Metadata { get; init; }

    /// <summary>
    /// A hash of the file and its <c>.meta</c>.
    /// </summary>
    public ulong Version { get; init; }

    /// <summary>
    /// What it made, or why not; none until then.
    /// </summary>
    public Result<T>? Asset { get; init; }

    /// <summary>
    /// Where its file is on disk.
    /// </summary>
    internal string File { get; init; }

    /// <summary>
    /// Its <c>.meta</c> without the id and the type.
    /// </summary>
    internal JsonElement Settings { get; init; }

    /// <summary>
    /// The gem that reads its type; none when Core does.
    /// </summary>
    internal IAssetLoader<T>? Loader { get; init; }

    internal IProgress<LoadProgress>? Progress { get; init; }

    internal bool IsFailed => Asset is { Ok: false };

    /// <summary>
    /// This load, failed: its file is not a <typeparamref name="T"/>, as <paramref name="why"/> says.
    /// </summary>
    internal AssetLoad<T> ToNotAsset(string why) => ToFailed($"{Path} is not a {typeof(T).Name}: {why}");

    internal AssetLoad<T> ToFailed(string error) => this with { Asset = (Result<T>)Result.Failure(error) };
}
