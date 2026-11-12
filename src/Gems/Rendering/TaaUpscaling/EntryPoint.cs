using Magic.Assets;
using Magic.Projects;

namespace Magic.TaaUpscaling;

/// <summary>
/// The gem: its <see cref="Renderer"/> upscales the finished picture with temporal anti-aliasing.
/// </summary>
public sealed class EntryPoint(AssetManager assets) : IGem
{
    public Renderer Renderer { get; } = new(assets);
}
