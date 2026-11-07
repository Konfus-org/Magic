using Magic.Ecs;
using Magic.Ecs.Components;

namespace Magic.GIRendering;

/// <summary>
/// Everything a frame draws, copied on the game thread as its frame ends and published for the render thread, which
/// reads nothing else of the game thread's. Filled whole each time, into the same lists.
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

    /// <summary>
    /// The pixel radius on screen under which an instance hands off to its impostor; 0 draws no impostors.
    /// </summary>
    public float ImpostorPixels { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}
