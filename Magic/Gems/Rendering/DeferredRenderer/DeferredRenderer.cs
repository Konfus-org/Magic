using Magic.Contexts.Assets;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace DeferredRendererGem;

/// <summary>
/// The gem: the scene renderer, one system on the Render phase, drawing through whatever <see cref="IRendering"/>
/// is loaded into whatever windows there are, and the pipeline's passes with their parameters for the settings window
/// (<see cref="IPipelineTuning"/>). Without a renderer gem it loads and draws nothing.
/// </summary>
internal sealed class DeferredRenderer : IGem, IPipelineTuning
{
    private readonly RenderSystem _system;
    private readonly Subscription _scheduled;

    public DeferredRenderer(IEcs ecs, Assets assets, IFileSystem files, Project project, Settings values, LodSettings lod, DeferredSettings settings, Threads threads, IWindowRegistry? windows, IRendering? rendering, IShaderCompiler? compiler, ITelemetry telemetry, IDebugUI debug)
    {
        _system = new RenderSystem(ecs, assets, files, project, values, lod, settings, windows, rendering, compiler, threads, telemetry, debug);
        _scheduled = ecs.Add(_system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }

    public IReadOnlyList<TunablePass> Passes => PipelineTuning.PassesOf(_system.Context);
}
