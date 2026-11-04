using Magic.Caching;
using Magic.Pipes;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Reads what was made of the file before, by its id and version, when its loader is also an <see cref="ICache{T}"/>.
/// </summary>
internal sealed class ReadCachePipeline<T>(Caches caches) : IPipeline<AssetLoad<T>>
    where T : IAsset<T>
{
    public async ValueTask<AssetLoad<T>> RunAsync(AssetLoad<T> state, CancellationToken cancel)
    {
        if (state.IsFailed || state.Loader is not ICache<T> cache)
            return state;

        Result<T> cached = await caches.ReadAsync(state.Id, state.Version, cache, cancel).ConfigureAwait(false);

        return cached.Ok ? state with { Asset = cached, IsCached = true } : state;
    }
}
