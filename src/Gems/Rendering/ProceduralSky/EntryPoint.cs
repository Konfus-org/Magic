using Magic.Assets;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Projects;

namespace Magic.ProceduralSky;

/// <summary>
/// Makes the sky's tables of the Earth's air for each view while an entity has a sky.
/// </summary>
public sealed class EntryPoint(AssetManager assets, IEcs ecs, IGraphicsApi gpu) : IGem
{
    public Renderer Renderer { get; } = new(assets, ecs, gpu);

    public void LateUpdate(in Frame frame) => Renderer.PublishSkies();
}
