using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Files;
using Magic.Graphics;
using Magic.Input;
using Magic.Projects;
using Magic.Threading;

namespace Magic.DeferredRendering;

/// <summary>
/// Keeps the world on the GPU for the gems that draw it, and lights it with deferred shading.
/// </summary>
/// <remarks>
/// The debug window also sets <see cref="DebugViewState"/> when a gem provides the debug UI.
/// </remarks>
public sealed class EntryPoint : IGem, IDisposable
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
        Renderer = new(settings, DebugViewState, shaders, gpu, assets, ecs, world, threads, events, files);
    }

    /// <summary>
    /// How the world is drawn. Game thread.
    /// </summary>
    public DeferredRenderingSettings Settings { get; }

    /// <summary>
    /// The part of the frame that the renderer shows, which the debug window and the console set. Game thread.
    /// </summary>
    public DebugViewState DebugViewState { get; } = new();

    public Renderer Renderer { get; }

    public void Dispose() => Renderer.Dispose();

    public void Update(in Frame frame) => _debugWindow?.Update(Settings, DebugViewState);

    public void LateUpdate(in Frame frame) => Renderer.PublishInputs(frame);
}
