using System.ComponentModel;
using Magic.Configuration;
using Magic.Input;

namespace Magic.DebugTools;

/// <summary>
/// The keys that show the debug lines and windows, and how often their numbers refresh.
/// </summary>
[Category("Debug")]
public sealed class DebugToolsSettings : ISettings
{
    /// <summary>
    /// The key that shows and hides the counter line.
    /// </summary>
    public Control CounterKey { get; set; } = Control.KeyF1;

    /// <summary>
    /// The key that opens and closes the stats window.
    /// </summary>
    public Control StatsKey { get; set; } = Control.KeyF3;

    /// <summary>
    /// The key that opens and closes the settings window.
    /// </summary>
    public Control SettingsKey { get; set; } = Control.KeyF4;

    /// <summary>
    /// Seconds between refreshes of the counter's and the stats window's numbers.
    /// </summary>
    public float StatsCollectSec { get; set; } = 0.5f;
}
