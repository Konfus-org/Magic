using System.ComponentModel;
using Magic.Input;

namespace Magic.DebugTools;

/// <summary>
/// Which of the debug lines and windows show, the keys that show them, and how often their numbers refresh.
/// </summary>
[Category("Debug")]
public sealed record DebugToolsSettings
{
    /// <summary>
    /// Whether the line of the frame rate, the frame time and the memory shows in the top left corner. The counter key
    /// turns it on and off.
    /// </summary>
#if DEBUG
    public bool IsCounterShown { get; init; } = true;
#else
    public bool IsCounterShown { get; init; }
#endif

    /// <summary>
    /// Whether the stats window, every instrument of the meters, is open. The stats key opens and closes it.
    /// </summary>
    public bool IsStatsOpen { get; init; }

    /// <summary>
    /// Whether the window of every gem's settings is open, where each is changed and saved. The settings key opens and
    /// closes it.
    /// </summary>
    public bool IsSettingsOpen { get; init; }

    /// <summary>
    /// The key that shows and hides the counter line.
    /// </summary>
    public Control CounterKey { get; init; } = Control.KeyF1;

    /// <summary>
    /// The key that opens and closes the stats window.
    /// </summary>
    public Control StatsKey { get; init; } = Control.KeyF3;

    /// <summary>
    /// The key that opens and closes the settings window.
    /// </summary>
    public Control SettingsKey { get; init; } = Control.KeyF4;

    /// <summary>
    /// Seconds between refreshes of the counter's and the stats window's numbers.
    /// </summary>
    public float StatsCollectSec { get; init; } = 0.5f;
}
