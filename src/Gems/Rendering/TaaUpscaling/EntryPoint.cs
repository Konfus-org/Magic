using Magic.Assets;
using Magic.Projects;

namespace Magic.TaaUpscaling;

/// <summary>
/// The gem: its <see cref="Renderer"/> upscales the finished picture with temporal anti-aliasing. It is public because
/// nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class EntryPoint(AssetManager assets) : IGem
{
    /// <summary>
    /// The upscaling pass and its two resolved pictures.
    /// </summary>
    public Renderer Renderer { get; } = new(assets);
}
