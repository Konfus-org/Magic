using System.Diagnostics;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Input;

namespace Magic.DebugTools;

/// <summary>
/// The debug UI's lines and windows over what the meters measure, each shown by its setting (<see cref="Settings"/>),
/// which its key, also a setting, turns on and off: the frame rate and memory line (F1), on from the start in a Debug
/// build, the stats window (F3) and the window of every gem's settings (F4). It listens to the meters only while the
/// counter or the stats window shows, and collects what they measured as often as
/// <see cref="DebugToolsSettings.StatsCollectSec"/> says. They are drawn with <see cref="IDebugUI"/> in
/// <see cref="Update"/>. Without input no key works.
/// </summary>
public sealed class DebugWindows(DebugToolsSettings settings, Settings allSettings, IDebugUI ui, IInput? input)
    : IHasSettings<DebugToolsSettings>,
        IDisposable
{
    private readonly MetricsReader _metrics = new();

    private readonly StatsWindow _stats = new(ui);

    private readonly SettingsWindow _settingsWindow = new(ui, allSettings);

    /// <summary>
    /// When the meters were last collected, 0 before the first time.
    /// </summary>
    private long _collectedTimestamp;

    /// <summary>
    /// Which lines and windows show, which the keys and the windows' close buttons also set. Game thread.
    /// </summary>
    public DebugToolsSettings Settings { get; set; } = settings;

    public void Dispose() => _metrics.Dispose();

    /// <summary>
    /// Turns the lines and windows on and off by their keys, collects the meters when due, and draws what shows.
    /// </summary>
    public void Update()
    {
        if (input is not null && input[Settings.CounterKey].WasPressed)
            Settings = Settings with { IsCounterShown = !Settings.IsCounterShown };

        if (input is not null && input[Settings.StatsKey].WasPressed)
            Settings = Settings with { IsStatsOpen = !Settings.IsStatsOpen };

        if (input is not null && input[Settings.SettingsKey].WasPressed)
            Settings = Settings with { IsSettingsOpen = !Settings.IsSettingsOpen };

        // Collect what the meters measured every so often, while anything shows.
        bool isListening = Settings.IsCounterShown || Settings.IsStatsOpen;
        _metrics.Listen(isListening);
        if (isListening && Stopwatch.GetElapsedTime(_collectedTimestamp).TotalSeconds >= Settings.StatsCollectSec)
        {
            _collectedTimestamp = Stopwatch.GetTimestamp();
            _stats.Refresh(_metrics.Collect());
        }

        if (Settings.IsCounterShown)
            ui.Text(_stats.CounterLine);

        bool isStatsOpen = Settings.IsStatsOpen;
        _stats.Update(ref isStatsOpen);
        if (isStatsOpen != Settings.IsStatsOpen)
            Settings = Settings with { IsStatsOpen = isStatsOpen };

        // Last, as a row may set this gem's own settings; only the close button is taken back from it.
        bool wasSettingsOpen = Settings.IsSettingsOpen;
        bool isSettingsOpen = wasSettingsOpen;
        _settingsWindow.Update(ref isSettingsOpen);
        if (isSettingsOpen != wasSettingsOpen)
            Settings = Settings with { IsSettingsOpen = isSettingsOpen };
    }
}
