using System.Globalization;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Contexts.Logging;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.DebugTools;

/// <summary>
/// The debug UI's windows, each opened and closed by its key: the console (the key left of 1), with the log and a line
/// to run commands on; the telemetry (F3), with what the engine measures; and the settings (F4), with every gem's
/// settings to change while running and the preset; and the frame rate in the top left corner while the settings say.
/// They are drawn with <see cref="IDebugUI"/>'s widgets in Update, so the gem takes the UI and is made after it. It is
/// a log sink, so the console has every line logged from the moment the gem is made. Without an input gem no key opens
/// a window; without a window Copy has no clipboard; without a commands gem the console only shows the log and adds no
/// commands.
/// </summary>
internal sealed class DebugToolsGem(
    DebugToolsSettings settings,
    IDebugUI ui,
    Settings gemSettings,
    Assets assets,
    App app,
    Events events,
    ICommands? commands = null,
    IInput? input = null,
    IWindow? window = null) : IGem, ILogSink, IDisposable
{
    private readonly ConsoleWindow _console = new(
        new ToolWindow("Console", Control.KeyGrave, ui, input, window),
        ui,
        commands);
    private readonly TelemetryWindow _telemetry = new(
        new ToolWindow("Telemetry", Control.KeyF3, ui, input, window),
        ui,
        settings,
        events);
    private readonly SettingsWindow _settings = new(
        new ToolWindow("Settings", Control.KeyF4, ui, input, window),
        ui,
        gemSettings,
        assets,
        commands,
        app.Quitting);

    /// <summary>
    /// The frame the frame rate was last worked out at, which the next is worked out from, none while it is not shown;
    /// and what it read.
    /// </summary>
    private Frame? _countedFrom;
    private string _frameRate = string.Empty;

    public void Dispose()
    {
        _console.Dispose();
        _telemetry.Dispose();
        _settings.Dispose();
    }

    public void Update(in Frame frame)
    {
        ShowFrameRate(frame);
        _console.Update();
        _telemetry.Update();
        _settings.Update();
    }

    public void Write(in LogLine line) => _console.Add(line);

    /// <summary>
    /// The frame rate and a frame's time, over the last half second, worked out twice a second so it can be read.
    /// </summary>
    private void ShowFrameRate(in Frame frame)
    {
        if (!settings.IsFrameRateShown)
        {
            _countedFrom = null;

            return;
        }

        // Counted from the frame it was last worked out at, or, shown anew, from now.
        _countedFrom ??= frame;
        double sec = frame.RealSec - _countedFrom.Value.RealSec;
        ulong frames = frame.Index - _countedFrom.Value.Index;
        if (sec >= 0.5)
        {
            _frameRate = string.Create(
                CultureInfo.InvariantCulture,
                $"{frames / sec:0} fps  {sec * 1000 / frames:0.00} ms");
            _countedFrom = frame;
        }

        if (_frameRate.Length > 0) ui.Text(_frameRate);
    }
}
