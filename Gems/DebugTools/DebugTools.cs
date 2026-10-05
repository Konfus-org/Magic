using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace DebugToolsGem;

/// <summary>
/// The gem: the console (grave), the settings window (F4, with the render pipeline's passes when a renderer exports
/// them) and the stats window (F3) in Update, and in Overlay, after the scene is recorded, the text reported in the
/// world and the list down the left of the window. Without an input gem the windows cannot be opened; without a window
/// gem the world text has nowhere to go and the windows' Copy has no clipboard.
/// </summary>
internal sealed class DebugTools : IGem
{
    private readonly Disposables<ISystem> _systems;
    private readonly Disposables<IDisposable> _scheduled;

    public DebugTools(IEcs ecs, Project project, Scheduler scheduler, IInput? input, IWindowRegistry? windows, IClipboard? clipboard, IPipelineTuning? pipeline)
    {
        ConsoleWindow console = new(input, clipboard, project.Console);
        SettingsWindow settings = new(project.Settings, pipeline, input, clipboard);
        StatsWindow stats = new(input, clipboard);
        WorldText worldText = new(ecs, windows, project.Settings.Render);
        ScreenLog screenLog = new();

        _systems = new Disposables<ISystem>(worldText, screenLog, stats, settings, console);

        ISystem[] order = [console, settings, stats, worldText, screenLog];
        _scheduled = new Disposables<IDisposable>([.. order.Select(system => scheduler.Add(ecs, system))]);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _systems.Dispose();
    }
}
