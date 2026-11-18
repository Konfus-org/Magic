using Magic.Assets;
using Magic.CommandLine;
using Magic.DeferredRendering;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Projects;

namespace Magic.ProceduralSky;

/// <summary>
/// Makes the sky's tables of the Earth's air for each view while an entity has a sky, and adds the <c>sky</c> subject
/// of <c>query</c>, which prints the world's lights and the sky's light on a surface.
/// </summary>
public sealed class EntryPoint : IGem
{
    public EntryPoint(
        AssetManager assets,
        IEcs ecs,
        IGraphicsApi gpu,
        ConsoleCommands commands,
        DeferredRenderingSettings settings
    )
    {
        commands.RegisterQuery(
            "sky",
            "Prints the world's directional lights, which of them lights the world, and the factors of the sky's light on a surface",
            () => SkyQuery.Describe(ecs, settings)
        );
        Renderer = new Renderer(assets, ecs, gpu);
    }

    public Renderer Renderer { get; }

    public void LateUpdate(in Frame frame) => Renderer.PublishSkies();
}
