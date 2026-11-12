using Magic.Assets;
using Magic.Projects;

namespace Magic.TracedShadows;

/// <summary>
/// The gem: its <see cref="Renderer"/> traces how much of each light reaches each pixel, as its <see cref="Settings"/>
/// say. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class EntryPoint(TracedShadowsSettings settings, AssetManager assets) : IGem
{
    /// <summary>
    /// How the shadows are traced, which the renderer reads each frame. Game thread.
    /// </summary>
    public TracedShadowsSettings Settings { get; } = settings;

    /// <summary>
    /// The shadow passes and the denoiser's targets.
    /// </summary>
    public Renderer Renderer { get; } = new(settings, assets);
}
