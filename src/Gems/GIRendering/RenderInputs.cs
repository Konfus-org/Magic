using Magic.Ecs;
using Magic.Ecs.Components;

namespace Magic.GIRendering;

/// <summary>
/// Everything a frame draws, copied on the main thread as its frame ends and published for the render thread, which
/// reads nothing else of the main thread's but each entity's shading (<c>World.GetShading</c>). Filled whole each time,
/// into the same lists.
/// </summary>
internal sealed class RenderInputs
{
    public EcsSnapshot<Transform, Renderer> Renderers { get; } = new();

    public EcsSnapshot<Transform, Camera> Cameras { get; } = new();

    public EcsSnapshot<Transform, DirectionalLight> DirectionalLights { get; } = new();

    public EcsSnapshot<Transform, PointLight> PointLights { get; } = new();

    public EcsSnapshot<Transform, SpotLight> SpotLights { get; } = new();

    public EcsSnapshot<Transform, RectLight> RectLights { get; } = new();

    public EcsSnapshot<Sky> Skies { get; } = new();

    public DebugView DebugView { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}
