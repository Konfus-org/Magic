using Magic.Assets;
using Magic.Projects;
using Magic.Threading;

namespace Magic.ClusteredGeometry;

/// <summary>
/// The gem: <see cref="Renderer"/> draws every model the world's view holds as clusters, and far away as its impostor,
/// over its <see cref="Settings"/>.
/// </summary>
public sealed class EntryPoint(ClusteredGeometrySettings settings, AssetManager assets, Threads threads)
    : IGem,
        IDisposable
{
    /// <summary>
    /// When a model draws as its impostor. Game thread.
    /// </summary>
    public ClusteredGeometrySettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets, threads);

    public void Dispose() => Renderer.Dispose();
}
