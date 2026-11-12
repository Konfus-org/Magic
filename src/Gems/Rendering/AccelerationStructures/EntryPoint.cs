using Magic.Assets;
using Magic.Projects;

namespace Magic.AccelerationStructures;

/// <summary>
/// The gem: its <see cref="Renderer"/> builds the world as rays see it.
/// </summary>
public sealed class EntryPoint(AssetManager assets) : IGem
{
    public Renderer Renderer { get; } = new(assets);
}
