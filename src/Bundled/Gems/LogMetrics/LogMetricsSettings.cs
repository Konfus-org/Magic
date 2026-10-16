using System.ComponentModel;
using Magic.Interfaces;

namespace Magic.LogMetrics;

/// <summary>
/// What LogMetrics logs and how often. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set LogMetrics.enabled=true</c>.
/// </summary>
[Category("Debug")]
internal sealed class LogMetricsSettings : ISettings
{
    /// <summary>
    /// Off unless asked for, so a project can list the gem and only the runs that want metrics log them.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How often a summary is logged, in seconds.
    /// </summary>
    public double IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// The meters listened to, each with the meters under it: "Magic" is Core's "Magic.Core" and every gem's "Magic.Gems.&lt;Gem&gt;".
    /// </summary>
    public IReadOnlyList<string> Meters { get; set; } = ["Magic"];
}
