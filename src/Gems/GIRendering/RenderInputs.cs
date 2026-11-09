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

    public EcsSnapshot<PostProcessing> PostProcessing { get; } = new();

    public EcsSnapshot<Weather> Weather { get; } = new();

    /// <summary>
    /// The number of the world the frame is of, a new one each time a domain replaces it, so the render thread can tell
    /// the first frame of another world.
    /// </summary>
    public int WorldNumber { get; set; }

    public DebugView DebugView { get; set; }

    /// <summary>
    /// The pixel radius on screen under which an instance hands off to its impostor; 0 hands off by distance alone.
    /// </summary>
    public float ImpostorPixels { get; set; }

    /// <summary>
    /// The distance in metres to an instance past which it hands off to its impostor; 0 hands off by size alone.
    /// </summary>
    public float ImpostorDistance { get; set; }
}
