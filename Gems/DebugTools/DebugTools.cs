using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace DebugToolsGem;

/// <summary>
/// The gem: the console (grave), the settings window (F4) and the debug display (F3) in Update, and in Overlay, after
/// the scene is recorded, the text reported in the world and the list down the left of the window. Without an input
/// gem the windows cannot be opened; without a window gem the world text has nowhere to go.
/// </summary>
internal sealed class DebugTools : IGem
{
    private readonly Disposables<ISystem> _systems;
    private readonly Disposables<IDisposable> _scheduled;

    public DebugTools(IEcs ecs, Project project, Scheduler scheduler, IInput? input, IWindowRegistry? windows)
    {
        ConsoleSystem console = new(input, project.Console);
        SettingsSystem settings = new(project.Settings, input);
        DebuggerDisplaySystem debugger = new(input);
        DebugUI3DSystem worldText = new(ecs, windows, project.Settings.Render);
        ScreenLogSystem screenLog = new();

        _systems = new Disposables<ISystem>(worldText, screenLog, debugger, settings, console);

        ISystem[] order = [console, settings, debugger, worldText, screenLog];
        _scheduled = new Disposables<IDisposable>([.. order.Select(system => scheduler.Add(ecs, system))]);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _systems.Dispose();
    }
}
