using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Graphics;
using Magic.Input;
using Magic.Projects;
using Magic.Threading;

namespace Magic.DeferredRendering;

/// <summary>
/// Keeps the world on the GPU for the gems that draw it, and lights it with deferred shading.
/// </summary>
/// <remarks>
/// The debug window also sets <see cref="Settings"/> when a gem provides the debug UI.
/// </remarks>
public sealed class EntryPoint(
    DeferredRenderingSettings settings,
    IShaderCompiler shaders,
    IGraphicsApi gpu,
    AssetManager assets,
    IEcs ecs,
    Threads threads,
    EventCoordinator events,
    IDebugUI? debugUI = null,
    IInput? input = null
) : IGem, IDisposable
{
    private readonly RendererDebugWindow? _debugWindow = debugUI is null ? null : new(debugUI, input);

    /// <summary>
    /// How the world is drawn and what the renderer shows. Game thread.
    /// </summary>
    public DeferredRenderingSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, shaders, gpu, assets, ecs, threads, events);

    public void Dispose() => Renderer.Dispose();

    public void Update(in Frame frame) => _debugWindow?.Update(Settings);

    public void LateUpdate(in Frame frame) => Renderer.PublishInputs(frame);
}
