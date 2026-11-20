using System.Diagnostics;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Input;
using Magic.Utils;

namespace Magic.DebugTools;

/// <summary>
/// Draws the debug lines and windows over what the meters measure, each shown by its setting
/// (<see cref="DebugToolsSettings"/>).
/// </summary>
/// <remarks>
/// A key, also a setting, turns each on and off: the frame rate and memory line (F1), on from the start in a Debug
/// build, the stats window (F3) and the window of every gem's settings (F4). Without input, no key works. It listens to
/// the meters only while the counter or the stats window shows, and collects them as often as
/// <see cref="DebugToolsSettings.StatsCollectSec"/> says.
/// </remarks>
internal sealed class DebugWindows : Disposable
{
    private readonly DebugToolsSettings _settings;

    private readonly OpenWindows _open;

    private readonly IDebugUI _ui;

    private readonly IInput? _input;

    private readonly MetricsReader _metrics;

    private readonly StatsWindow _stats;

    private readonly SettingsWindow _settingsWindow;

    /// <summary>
    /// When the meters were last collected, 0 before the first time.
    /// </summary>
    private long _collectedTimestamp;

    public DebugWindows(DebugToolsSettings settings, OpenWindows open, Settings allSettings, IDebugUI ui, IInput? input)
    {
        _settings = settings;
        _open = open;
        _ui = ui;
        _input = input;
        _metrics = new MetricsReader();
        Track(_metrics);
        _stats = new StatsWindow(ui);
        _settingsWindow = new SettingsWindow(ui, allSettings);
    }

    /// <summary>
    /// Turns the lines and windows on and off by their keys, collects the meters when due, and draws what shows.
    /// </summary>
    internal void Update()
    {
        if (_input is not null && _input[_settings.CounterKey].WasPressed)
            _open.IsCounterShown = !_open.IsCounterShown;

        if (_input is not null && _input[_settings.StatsKey].WasPressed)
            _open.IsStatsOpen = !_open.IsStatsOpen;

        if (_input is not null && _input[_settings.SettingsKey].WasPressed)
            _open.IsSettingsOpen = !_open.IsSettingsOpen;

        // Collect what the meters measured every so often, while anything shows.
        bool isListening = _open.IsCounterShown || _open.IsStatsOpen;
        _metrics.Listen(isListening);
        if (isListening && Stopwatch.GetElapsedTime(_collectedTimestamp).TotalSeconds >= _settings.StatsCollectSec)
        {
            _collectedTimestamp = Stopwatch.GetTimestamp();
            _stats.Refresh(_metrics.Collect());
        }

        bool isStatsOpen = _open.IsStatsOpen;
        _stats.Update(ref isStatsOpen);
        _open.IsStatsOpen = isStatsOpen;

        bool isSettingsOpen = _open.IsSettingsOpen;
        _settingsWindow.Update(ref isSettingsOpen);
        _open.IsSettingsOpen = isSettingsOpen;
    }

    /// <summary>
    /// Has the debug UI show the frame rate and memory line, while it shows, from the next frame.
    /// </summary>
    /// <remarks>
    /// Call it after the debug UI took the frame's lines: the line is then the first reported for the next frame, so it
    /// shows over every line that Core and the gems report in that frame.
    /// </remarks>
    internal void ReportCounter()
    {
        if (_open.IsCounterShown)
            _ui.Text(_stats.CounterLine);
    }
}
