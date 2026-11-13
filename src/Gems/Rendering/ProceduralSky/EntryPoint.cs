using Magic.Assets;
using Magic.Ecs;
using Magic.Projects;

namespace Magic.ProceduralSky;

/// <summary>
/// Makes the sky's tables of the Earth's air for each view while an entity has a sky.
/// </summary>
public sealed class EntryPoint(AssetManager assets, IEcs ecs) : IGem
{
    public Renderer Renderer { get; } = new(assets, ecs);

    public void LateUpdate(in Frame frame) => Renderer.PublishSkies();
}
