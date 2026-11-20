using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Files;
using Magic.Graphics;
using Magic.Input;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.DeferredRendering;

/// <summary>
/// Keeps the world on the GPU for the gems that draw it, and lights it with deferred shading.
/// </summary>
/// <remarks>
/// The debug window also sets <see cref="RendererState"/> when a gem provides the debug UI.
/// </remarks>
public sealed class EntryPoint : Disposable, IGem
{
    private readonly RendererDebugWindow? _debugWindow;

    public EntryPoint(
        DeferredRenderingSettings settings,
        IShaderCompiler shaders,
        IGraphicsApi gpu,
        AssetManager assets,
        IEcs ecs,
        Worlds.World world,
        Threads threads,
        EventCoordinator events,
        IFileSystem files,
        IDebugUI? debugUI = null,
        IInput? input = null
    )
    {
        _debugWindow = debugUI is null ? null : new(debugUI, input);
        Settings = settings;
        State = new(ecs);
        Renderer = new(settings, State, shaders, gpu, assets, ecs, world, threads, events, files);
        Track(Renderer);
    }

    /// <summary>
    /// How the world is drawn. Game thread.
    /// </summary>
    public DeferredRenderingSettings Settings { get; }

    /// <summary>
    /// What the renderer shows and what lights it, which the debug window and the console reach. Game thread.
    /// </summary>
    public RendererState State { get; }

    public Renderer Renderer { get; }

    public void Update(in Frame frame) => _debugWindow?.Update(Settings, State);

    public void LateUpdate(in Frame frame) => Renderer.PublishInputs(frame);
}
