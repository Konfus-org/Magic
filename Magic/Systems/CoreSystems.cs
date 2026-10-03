using Magic.Interfaces;
using Magic.Services;
using Magic.Systems.DebugUI;
using Magic.Systems.Rendering;
using Magic.Systems.Streaming;
using Magic.Utils;

namespace Magic.Systems;

/// <summary>
/// The host's own systems and their places on the schedule. Nothing outside reads a system: they are made here, run
/// by the ECS gem in their phases, and disposed with this, schedule first.
/// </summary>
internal sealed class CoreSystems : IDisposable
{
    private readonly Disposables<ISystem> _systems;
    private readonly Disposables<IDisposable> _scheduled;

    private CoreSystems(Container container, IEcs ecs)
    {
        container.TryGet(out IInput? input);
        container.TryGet(out IWindowRegistry? windows);
        container.TryGet(out IRendering? rendering);
        Scheduler scheduler = container.Get<Scheduler>();
        Assets assets = container.Get<Assets>();
        Project project = container.Get<Project>();
        Threads threads = container.Get<Threads>();

        ScriptSystem scripts = new(ecs, assets, scheduler, container); // its later phases' hooks go on the schedule here, ahead of transforms and rendering
        StreamingSystem streaming = new(ecs, assets, project, scripts, container.Get<World>(), threads, rendering);
        TagSystem tags = new(ecs);
        TransformSystem transforms = new(ecs);
        RenderSystem renderer = new(ecs, assets, container.Get<IFileSystem>(), project, windows, rendering, threads);
        ConsoleSystem console = new(input, project.Console);
        SettingsSystem settings = new(project.Settings, input);
        DebuggerDisplaySystem debugger = new(transforms, renderer, streaming, assets, input);
        DebugUI3DSystem worldText = new(ecs, windows, project.Settings.Render);
        ScreenLogSystem screenLog = new();

        // Disposed in this order: scripts before streaming destroys the entities under them.
        _systems = new Disposables<ISystem>(worldText, screenLog, renderer, transforms, tags, scripts, streaming, debugger, settings, console);

        // Scheduled in the order they run within a phase.
        ISystem[] order = [console, settings, streaming, scripts, debugger, tags, transforms, renderer, worldText, screenLog];
        _scheduled = new Disposables<IDisposable>([.. order.Select(system => scheduler.Add(ecs, system))]);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _systems.Dispose();
    }

    /// <summary>
    /// The systems, once the gems are loaded, added to the scheduler in the order they run within a phase. They need
    /// the ECS gem; without one there are none. The gems that provide the rest are static, so the systems keep what
    /// they are given here (null when no gem provides it).
    /// </summary>
    public static CoreSystems? Create(Container container)
    {
        if (!container.TryGet(out IEcs? ecs))
        {
            Debugging.Log.Warn("No loaded gem provides IEcs: nothing will be streamed, transformed or rendered.");
            return null;
        }

        return new CoreSystems(container, ecs);
    }
}
