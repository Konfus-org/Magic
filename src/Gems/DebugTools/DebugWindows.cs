using System.Diagnostics;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Input;

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
internal sealed class DebugWindows(DebugToolsSettings settings, Settings allSettings, IDebugUI ui, IInput? input)
    : IDisposable
{
    private readonly MetricsReader _metrics = new();

    private readonly StatsWindow _stats = new(ui);

    private readonly SettingsWindow _settingsWindow = new(ui, allSettings);

    /// <summary>
    /// When the meters were last collected, 0 before the first time.
    /// </summary>
    private long _collectedTimestamp;

    public void Dispose() => _metrics.Dispose();

    /// <summary>
    /// Turns the lines and windows on and off by their keys, collects the meters when due, and draws what shows.
    /// </summary>
    internal void Update()
    {
        if (input is not null && input[settings.CounterKey].WasPressed)
            settings.IsCounterShown = !settings.IsCounterShown;

        if (input is not null && input[settings.StatsKey].WasPressed)
            settings.IsStatsOpen = !settings.IsStatsOpen;

        if (input is not null && input[settings.SettingsKey].WasPressed)
            settings.IsSettingsOpen = !settings.IsSettingsOpen;

        // Collect what the meters measured every so often, while anything shows.
        bool isListening = settings.IsCounterShown || settings.IsStatsOpen;
        _metrics.Listen(isListening);
        if (isListening && Stopwatch.GetElapsedTime(_collectedTimestamp).TotalSeconds >= settings.StatsCollectSec)
        {
            _collectedTimestamp = Stopwatch.GetTimestamp();
            _stats.Refresh(_metrics.Collect());
        }

        if (settings.IsCounterShown)
            ui.Text(_stats.CounterLine);

        bool isStatsOpen = settings.IsStatsOpen;
        _stats.Update(ref isStatsOpen);
        if (isStatsOpen != settings.IsStatsOpen)
            settings.IsStatsOpen = isStatsOpen;

        // Last, as a row may set these windows' own settings; only the close button is taken back from it.
        bool wasSettingsOpen = settings.IsSettingsOpen;
        bool isSettingsOpen = wasSettingsOpen;
        _settingsWindow.Update(ref isSettingsOpen);
        if (isSettingsOpen != wasSettingsOpen)
            settings.IsSettingsOpen = isSettingsOpen;
    }
}
