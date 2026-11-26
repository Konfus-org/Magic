using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;

namespace Magic.AmbientOcclusion;

/// <summary>
/// The gem: its <see cref="Renderer"/> finds how much of the ambient light reaches each pixel of each view, as its
/// <see cref="Settings"/> say.
/// </summary>
public sealed class EntryPoint(AmbientOcclusionSettings settings, AssetManager assets, IGraphicsApi gpu) : IGem
{
    /// <summary>
    /// How the ambient occlusion is found, which the renderer reads each frame. Game thread.
    /// </summary>
    public AmbientOcclusionSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets, gpu);
}
