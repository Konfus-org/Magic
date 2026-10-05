using Magic.Interfaces;
using Magic.Services;

namespace DeferredRendererGem;

/// <summary>
/// The gem: the scene renderer, one system on the Render phase, drawing through whatever <see cref="IRendering"/>
/// is loaded into whatever windows there are, and the pipeline's passes with their parameters for the settings window
/// (<see cref="IPipelineTuning"/>). Without a renderer gem it loads and draws nothing.
/// </summary>
internal sealed class DeferredRenderer : IGem, IPipelineTuning
{
    private readonly RenderSystem _system;
    private readonly IDisposable _scheduled;

    public DeferredRenderer(IEcs ecs, Assets assets, IFileSystem files, Project project, World world, Threads threads, Scheduler scheduler, IWindowRegistry? windows, IRendering? rendering)
    {
        _system = new RenderSystem(ecs, assets, files, project, world, windows, rendering, threads);
        _scheduled = scheduler.Add(ecs, _system);
    }

    public IReadOnlyList<TunablePass> Passes => _system.Tuning.Passes;

    public void Set(ulong pass, string name, Magic.Contexts.Assets.Param value)
    {
        _system.Tuning.Set(pass, name, value);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }
}
