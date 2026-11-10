using System.Diagnostics;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Input;
using Magic.Projects;

namespace Magic.DebugTools;

/// <summary>
/// The debug UI's lines and windows over what the meters measure, each shown by its setting (<see cref="Settings"/>),
/// which its key also turns on and off: the frame rate and memory line (F1), on from the start in a Debug build, and the
/// stats window (F3). It listens to the meters only while one shows, and collects what they measured twice a second.
/// They are drawn with <see cref="IDebugUI"/> in Update, so the gem takes the UI and is made after it. Without a gem
/// that provides input no key works. It is public because nothing in its assembly makes it: Magic does, by reflection,
/// when a project lists the gem.
/// </summary>
public sealed class DebugToolsGem(DebugToolsSettings settings, IDebugUI ui, IInput? input = null)
    : IGem,
        IHasSettings<DebugToolsSettings>,
        IDisposable
{
    private static readonly TimeSpan _collectEvery = TimeSpan.FromSeconds(0.5);

    private readonly MetricsReader _metrics = new();

    private readonly StatsWindow _stats = new(ui);

    /// <summary>
    /// When the meters were last collected, 0 before the first time.
    /// </summary>
    private long _collectedTimestamp;

    /// <summary>
    /// Which lines and windows show, which the keys and the stats window's close button also set. Game thread.
    /// </summary>
    public DebugToolsSettings Settings { get; set; } = settings;

    public void Dispose() => _metrics.Dispose();

    public void Update(in Frame frame)
    {
        if (input is not null && input[Control.KeyF1].WasPressed)
            Settings = Settings with { IsCounterShown = !Settings.IsCounterShown };

        if (input is not null && input[Control.KeyF3].WasPressed)
            Settings = Settings with { IsStatsOpen = !Settings.IsStatsOpen };

        // Collect what the meters measured twice a second, while anything shows.
        bool isListening = Settings.IsCounterShown || Settings.IsStatsOpen;
        _metrics.Listen(isListening);
        if (isListening && Stopwatch.GetElapsedTime(_collectedTimestamp) >= _collectEvery)
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
    }
}
