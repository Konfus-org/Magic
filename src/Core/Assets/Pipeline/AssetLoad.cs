using System.Text.Json;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// The state of the pipeline that loads one <typeparamref name="T"/>, run on a worker, for several assets at once: the
/// asset's file as far as it has been read, and what was made of it. A gem joins by implementing
/// <see cref="Pipes.IPipeline{TState}"/> of it, to see or change every <typeparamref name="T"/> as it is loaded, cache
/// hits included, after Magic has made it. Keep nothing per load in the gem.
/// </summary>
public readonly record struct AssetLoad<T>
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
    /// What it made, or why not; none until then. Once it holds a failure, Magic's own pass the state on untouched.
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

    /// <summary>
    /// Whether <see cref="Asset"/> came from the cache, so it isn't written back.
    /// </summary>
    internal bool IsCached { get; init; }

    internal bool IsFailed => Asset is { Ok: false };

    /// <summary>
    /// This load, failed: its file is not a <typeparamref name="T"/>, as <paramref name="why"/> says.
    /// </summary>
    internal AssetLoad<T> ToNotAsset(string why) => ToFailed($"{Path} is not a {typeof(T).Name}: {why}");

    internal AssetLoad<T> ToFailed(string error) => this with { Asset = (Result<T>)Result.Failure(error) };
}
