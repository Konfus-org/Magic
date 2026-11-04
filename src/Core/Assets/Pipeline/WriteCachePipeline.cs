using Magic.Caching;
using Magic.Pipes;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Keeps what a loader that is also an <see cref="ICache{T}"/> made, by the file's id and version.
/// </summary>
internal sealed class WriteCachePipeline<T>(Caches caches) : IPipeline<AssetLoad<T>>
    where T : IAsset<T>
{
    public async ValueTask<AssetLoad<T>> RunAsync(AssetLoad<T> state, CancellationToken cancel)
    {
        if (!state.IsCached && state.Loader is ICache<T> cache && state.Asset is { Ok: true, Value: { } asset })
            await caches.WriteAsync(state.Id, state.Version, asset, cache, cancel).ConfigureAwait(false);

        return state;
    }
}
