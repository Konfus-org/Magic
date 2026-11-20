using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GlobalIllumination;

/// <summary>
/// The gem: its <see cref="Renderer"/> lights the view's surfaces indirectly, as its <see cref="Settings"/> say.
/// </summary>
public sealed class EntryPoint : Disposable, IGem
{
    public EntryPoint(GlobalIlluminationSettings settings, AssetManager assets, Threads threads, IGraphicsApi gpu)
    {
        Settings = settings;
        Renderer = new(settings, Terms, assets, threads, gpu);
        Track(Renderer);
    }

    /// <summary>
    /// How the light is traced, which the renderer reads each frame. Game thread.
    /// </summary>
    public GlobalIlluminationSettings Settings { get; }

    /// <summary>
    /// Which terms of the light are on, which the console turns off to measure each. The game thread writes it, and
    /// the render thread reads it.
    /// </summary>
    public LightTerms Terms { get; } = new();

    public Renderer Renderer { get; }
}
