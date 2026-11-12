using Magic.Assets;
using Magic.Projects;

namespace Magic.TracedShadows;

/// <summary>
/// The gem: its <see cref="Renderer"/> traces how much of each light reaches each pixel, as its <see cref="Settings"/>
/// say.
/// </summary>
public sealed class EntryPoint(TracedShadowsSettings settings, AssetManager assets) : IGem
{
    /// <summary>
    /// How the shadows are traced, which the renderer reads each frame. Game thread.
    /// </summary>
    public TracedShadowsSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets);
}
