using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;

namespace Magic.LightPropagationVolumes;

/// <summary>
/// The gem: its <see cref="Renderer"/> spreads the sun's bounced light through a grid around each view, as its
/// <see cref="Settings"/> say.
/// </summary>
public sealed class EntryPoint(LightPropagationVolumesSettings settings, AssetManager assets, IGraphicsApi gpu) : IGem
{
    /// <summary>
    /// How the bounced light is spread, which the renderer reads each frame. Game thread.
    /// </summary>
    public LightPropagationVolumesSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets, gpu);
}
