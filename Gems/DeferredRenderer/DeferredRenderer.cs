using Magic.Interfaces;
using Magic.Services;

namespace DeferredRendererGem;

/// <summary>
/// The gem: the scene renderer, one system on the Render phase, drawing through whatever <see cref="IRendering"/>
/// is loaded into whatever windows there are. Without a renderer gem it loads and draws nothing.
/// </summary>
internal sealed class DeferredRenderer : IGem
{
    private readonly RenderSystem _system;
    private readonly IDisposable _scheduled;

    public DeferredRenderer(IEcs ecs, Assets assets, IFileSystem files, Project project, Threads threads, Scheduler scheduler, IWindowRegistry? windows, IRendering? rendering)
    {
        _system = new RenderSystem(ecs, assets, files, project, windows, rendering, threads);
        _scheduled = scheduler.Add(ecs, _system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }
}
