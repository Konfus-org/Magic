using Magic.Ecs;
using Magic.Ecs.Components;

namespace Magic.DeferredRendering.View;

/// <summary>
/// Everything a frame draws, copied on the game thread as its frame ends and published for the render thread.
/// </summary>
/// <remarks>
/// The render thread reads nothing else of the game thread's. It is filled whole each time, into the same lists.
/// </remarks>
internal sealed class RenderInputs
{
    public EcsSnapshot<Transform, Ecs.Components.Renderer> Renderers { get; } = new();

    public EcsSnapshot<Transform, Camera> Cameras { get; } = new();

    public EcsSnapshot<Transform, DirectionalLight> DirectionalLights { get; } = new();

    public EcsSnapshot<Transform, PointLight> PointLights { get; } = new();

    public EcsSnapshot<Transform, SpotLight> SpotLights { get; } = new();

    public EcsSnapshot<Transform, RectLight> RectLights { get; } = new();

    public EcsSnapshot<Sky> Skies { get; } = new();

    public EcsSnapshot<PostProcessing> PostProcessing { get; } = new();

    /// <summary>
    /// The number of the world the frame is of, new each time a domain replaces it.
    /// </summary>
    /// <remarks>
    /// The render thread tells the first frame of another world by it.
    /// </remarks>
    public int WorldNumber { get; set; }
}
