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
    internal EcsSnapshot<Transform, Ecs.Components.Renderer> Renderers { get; } = new();

    /// <summary>
    /// The physics bodies, of which the dynamic ones mark their renderers' instances as able to move in any frame.
    /// </summary>
    internal EcsSnapshot<Physics> Bodies { get; } = new();

    internal EcsSnapshot<Transform, Camera> Cameras { get; } = new();

    internal EcsSnapshot<Transform, DirectionalLight> DirectionalLights { get; } = new();

    internal EcsSnapshot<Transform, PointLight> PointLights { get; } = new();

    internal EcsSnapshot<Transform, SpotLight> SpotLights { get; } = new();

    internal EcsSnapshot<Transform, RectLight> RectLights { get; } = new();

    /// <summary>
    /// The number of the world the frame is of, new each time a domain replaces it.
    /// </summary>
    /// <remarks>
    /// The render thread tells the first frame of another world by it.
    /// </remarks>
    internal int WorldNumber { get; set; }
}
