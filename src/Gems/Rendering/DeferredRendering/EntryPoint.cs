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
/// The gem: <see cref="Renderer"/> keeps the world on the GPU for the gems that draw it and lights it, deferred, over
/// its <see cref="Settings"/>. It is public because nothing in its assembly makes it: Magic does, by reflection, when a
/// project lists the gem.
/// </summary>
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
    /// <summary>
    /// How the world is drawn and what the renderer shows. Game thread.
    /// </summary>
    public DeferredRenderingSettings Settings { get; } = settings;

    /// <summary>
    /// The world on the GPU, and its shading.
    /// </summary>
    public Renderer Renderer { get; } = new(settings, shaders, gpu, assets, ecs, threads, events, debugUI, input);

    public void Dispose() => Renderer.Dispose();

    public void Update(in Frame frame) => Renderer.UpdateDebugWindow();

    public void LateUpdate(in Frame frame) => Renderer.PublishInputs(frame);
}
