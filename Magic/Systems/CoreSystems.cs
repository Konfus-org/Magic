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
    private readonly ISystem[] _systems;
    private readonly IDisposable[] _scheduled;

    private CoreSystems(ISystem[] systems, IDisposable[] scheduled)
    {
        _systems = systems;
        _scheduled = scheduled;
    }

    public void Dispose()
    {
        foreach (IDisposable scheduled in _scheduled)
            scheduled.Dispose();

        foreach (ISystem system in _systems)
            system.Dispose();
    }

    /// <summary>
    /// The systems, once the gems are loaded, added to the scheduler in the order they run within a phase. They need
    /// the ECS gem; without one there are none. The gems that provide the rest are static, so the systems keep what
    /// they are given here (null when no gem provides it).
    /// </summary>
    public static CoreSystems? Create(IEcs? ecs, IInput? input, IWindowRegistry? windows, IRendering? rendering, Scheduler scheduler, Assets assets, IFileSystem files, Project project, Container container)
    {
        if (ecs is null)
        {
            Debugging.Log.Warn("No loaded gem provides IEcs: nothing will be streamed, transformed or rendered.");
            return null;
        }

        ScriptSystem scripts = new(ecs, assets, scheduler, container); // its later phases' hooks go on the schedule here, ahead of transforms and rendering
        StreamingSystem streaming = new(ecs, assets, project, scripts);
        TransformSystem transforms = new(ecs);
        RenderSystem renderer = new(ecs, assets, files, project, windows, rendering);
        ConsoleSystem console = new(input);
        SettingsSystem settings = new(project.Settings, input);
        DebuggerDisplaySystem debugger = new(transforms, renderer, streaming, assets, input);

        ISystem[] systems = [console, settings, streaming, scripts, debugger, transforms, renderer];
        IDisposable[] scheduled = [.. systems.Select(system => scheduler.Add(ecs, system))];

        // Disposed in this order: scripts before streaming destroys the entities under them.
        return new CoreSystems([renderer, transforms, scripts, streaming, debugger, settings, console], scheduled);
    }
}
