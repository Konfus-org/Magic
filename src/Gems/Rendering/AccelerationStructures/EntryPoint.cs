using Magic.Assets;
using Magic.Projects;

namespace Magic.AccelerationStructures;

/// <summary>
/// The gem: its <see cref="Renderer"/> builds the world as rays see it. It is public because nothing in its assembly
/// makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class EntryPoint(AssetManager assets) : IGem
{
    /// <summary>
    /// The acceleration structures and the hit materials table.
    /// </summary>
    public Renderer Renderer { get; } = new(assets);
}
