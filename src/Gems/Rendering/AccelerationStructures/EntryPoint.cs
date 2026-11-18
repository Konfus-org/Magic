using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;

namespace Magic.AccelerationStructures;

/// <summary>
/// Builds the world as rays see it.
/// </summary>
public sealed class EntryPoint(AssetManager assets, IGraphicsApi gpu) : IGem
{
    public Renderer Renderer { get; } = new(assets, gpu);
}
