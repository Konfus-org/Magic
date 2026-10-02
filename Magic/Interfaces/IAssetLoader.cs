using Magic.Contexts.Assets;
using Magic.Utils;

namespace Magic.Interfaces;

/// <summary>
/// Turns a file's bytes into an asset of one type. A gem provides one by implementing it; the asset manager asks the
/// container for the <see cref="IAssetLoader{T}"/> of the type it is loading.
/// </summary>
public interface IAssetLoader<T> where T : Asset
{
    /// <summary>
    /// Fills <paramref name="asset"/> from <paramref name="bytes"/>. The asset already carries its id, version, path
    /// and every property its sidecar set. A file it cannot make an asset of is a failure saying why, which the
    /// asset manager logs. Called on whatever thread loads it, the main thread or a worker.
    /// </summary>
    Result Load(T asset, byte[] bytes);
}
