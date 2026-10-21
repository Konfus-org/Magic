using Magic.Contexts.Utils;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// Work on an asset once its <see cref="IAssetLoader{T}"/> has made it, before anyone gets it, such as making its
/// levels of detail. A gem provides one for a type as it provides any interface, by implementing it;
/// <see cref="Assets"/> asks for the one of a type (through <see cref="IServiceProvider"/>) each time it loads one, and
/// runs it on every load and reload of the type.
/// </summary>
public interface ILoadPostProcessor<T>
    where T : IAsset<T>
{
    /// <summary>
    /// <paramref name="asset"/>, which has its handle, as everyone is to get it, or why it cannot be had. It may be a
    /// new object, which <see cref="Assets"/> gives the handle. <paramref name="version"/> is a hash of the asset's
    /// file and its <c>.meta</c>, which changes when either does: what a cache of what is made from the asset is
    /// checked against (<see cref="Caches"/>). On a worker, for several assets at once.
    /// </summary>
    Task<Result<T>> ProcessAsync(T asset, ulong version, CancellationToken cancel);
}
