using Magic.Contexts.Assets;

namespace Magic.Interfaces;

public interface IAssetLoader
{
}

/// <summary>
/// Turns a file's bytes into an asset of one type. A gem exports one with <c>[GemExport(typeof(IAssetLoader&lt;T&gt;))]</c>;
/// the host registers it by <typeparamref name="T"/> and drops it when the gem unloads.
/// </summary>
public interface IAssetLoader<T> : IAssetLoader where T : Asset
{
    /// <summary>
    /// Fills <paramref name="asset"/> from <paramref name="bytes"/> on the calling thread. The asset already
    /// carries its id, version, path and every property its sidecar set.
    /// </summary>
    void Load(T asset, byte[] bytes);

    /// <summary>
    /// The same, off the calling thread: report progress 0..1 and observe the token.
    /// </summary>
    Task LoadAsync(T asset, byte[] bytes, IProgress<double>? progress, CancellationToken cancellationToken);
}
