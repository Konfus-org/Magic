using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;

namespace Magic.LightProbes;

/// <summary>
/// The gem: its <see cref="Renderer"/> makes the sky's light for each view, as its <see cref="Settings"/> say.
/// </summary>
public sealed class EntryPoint(LightProbesSettings settings, AssetManager assets, IGraphicsApi gpu) : IGem
{
    /// <summary>
    /// How the sky's light is made, which the renderer reads each frame. Game thread.
    /// </summary>
    public LightProbesSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets, gpu);
}
