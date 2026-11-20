using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// The gem: <see cref="Renderer"/> draws every model the world's view holds as clusters, and far away as its impostor,
/// over its <see cref="Settings"/>.
/// </summary>
public sealed class EntryPoint : Disposable, IGem
{
    public EntryPoint(ClusteredGeometrySettings settings, AssetManager assets, Threads threads, IGraphicsApi gpu)
    {
        Settings = settings;
        Renderer = new(settings, assets, threads, gpu);
        Track(Renderer);
    }

    /// <summary>
    /// When a model draws as its impostor. Game thread.
    /// </summary>
    public ClusteredGeometrySettings Settings { get; }

    public Renderer Renderer { get; }
}
