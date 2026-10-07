using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace DebugToolsGem;

/// <summary>
/// The gem: the console (grave), the settings window (F4, with the render pipeline's passes when a renderer exports
/// them), the frame's own stats and the stats window (F3) in Update, and in Overlay, after the scene is recorded, the text reported in the
/// world and the list down the left of the window. Without an input gem the windows cannot be opened; without a window
/// gem the world text has nowhere to go and the windows' Copy has no clipboard. A logger, for the console.
/// </summary>
internal sealed class DebugTools : IGem, ILogger
{
    private readonly Action<LogLevel, string> _toConsole;
    private readonly Disposables<ISystem> _systems;
    private readonly Disposables<Subscription> _scheduled;

    public DebugTools(IEcs ecs, Settings values, Assets assets, IDebugWidgets ui, DebugUI debug, Telemetry telemetry, IInput? input, IWindowRegistry? windows, IClipboard? clipboard, IPipelineTuning? pipeline)
    {
        ConsoleWindow console = new(ui, debug, input, clipboard);
        _toConsole = console.Add;
        SettingsWindow settings = new(values, assets, pipeline, ui, debug, input, clipboard);
        FrameStats frameStats = new(telemetry);
        StatsWindow stats = new(ui, debug, telemetry, input, clipboard);
        WorldText worldText = new(ecs, ui, debug, windows);
        ScreenLog screenLog = new(ui, debug);

        _systems = new Disposables<ISystem>(worldText, screenLog, stats, frameStats, settings, console);

        ISystem[] order = [console, settings, frameStats, stats, worldText, screenLog];
        _scheduled = new Disposables<Subscription>([.. order.Select(system => ecs.Add(system))]);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _systems.Dispose();
    }

    public void Log(LogLevel level, string message, string file, int line)
    {
        _toConsole(level, message);
    }
}
