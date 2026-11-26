using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;

namespace Magic.ShadowMaps;

/// <summary>
/// The gem: its <see cref="Renderer"/> fits the shadow maps of each view, and shadows each pixel from them, as its
/// <see cref="Settings"/> say.
/// </summary>
public sealed class EntryPoint(ShadowMapsSettings settings, AssetManager assets, IGraphicsApi gpu) : IGem
{
    /// <summary>
    /// How the shadow maps are drawn and read, which the renderer reads each frame. Game thread.
    /// </summary>
    public ShadowMapsSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets, gpu);
}
