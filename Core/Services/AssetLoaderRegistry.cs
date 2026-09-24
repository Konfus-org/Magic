using Magic.Interfaces;
using Magic.Utils;

namespace Magic.Services;

/// <summary>
/// The loader per asset type, filled by the gem system as gems export <see cref="IAssetLoader{T}"/> and
/// emptied as they unload. First loader for a type wins; a later one is logged and ignored.
/// </summary>
internal static class AssetLoaderRegistry
{
    private static readonly Dictionary<Type, IAssetLoader> _registered = [];
    private static readonly Lock _lock = new();

    internal static void Register(Type assetType, IAssetLoader loader)
    {
        lock (_lock)
        {
            if (_registered.TryGetValue(assetType, out IAssetLoader? existing))
            {
                Debugging.LogWarning($"{assetType.Name} already has loader {existing.GetType().FullName}; ignoring {loader.GetType().FullName}.");
                return;
            }
            _registered[assetType] = loader;
        }
    }

    /// <summary>Removes <paramref name="loader"/> if it is the one registered for <paramref name="assetType"/>.</summary>
    internal static void Unregister(Type assetType, IAssetLoader loader)
    {
        lock (_lock)
        {
            if (_registered.TryGetValue(assetType, out IAssetLoader? existing) && existing == loader)
                _registered.Remove(assetType);
        }
    }

    internal static IAssetLoader<T>? Get<T>() where T : Contexts.Assets.Asset
    {
        lock (_lock)
        {
            return _registered.TryGetValue(typeof(T), out IAssetLoader? loader) ? loader as IAssetLoader<T> : null;
        }
    }
}
