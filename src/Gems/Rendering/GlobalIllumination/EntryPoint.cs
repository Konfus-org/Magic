using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;

namespace Magic.GlobalIllumination;

/// <summary>
/// The gem: its <see cref="Renderer"/> lights the view's surfaces indirectly, as its <see cref="Settings"/> say.
/// </summary>
public sealed class EntryPoint(
    GlobalIlluminationSettings settings,
    AssetManager assets,
    Threads threads,
    IGraphicsApi gpu
) : IGem
{
    /// <summary>
    /// How the light is traced, which the renderer reads each frame. Game thread.
    /// </summary>
    public GlobalIlluminationSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets, threads, gpu);
}
