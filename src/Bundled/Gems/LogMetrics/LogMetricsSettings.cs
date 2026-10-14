using System.ComponentModel;

namespace Magic.LogMetrics;

/// <summary>
/// What LogMetrics logs and how often. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set LogMetrics.enabled=true</c>.
/// </summary>
internal sealed class LogMetricsSettings
{
    /// <summary>
    /// Off unless asked for, so a project can list the gem and only the runs that want metrics log them.
    /// </summary>
    [Browsable(false)]
    public bool Enabled { get; set; }

    /// <summary>
    /// How often a summary is logged, in seconds.
    /// </summary>
    public double IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// The meters listened to, each with the meters under it: "Magic" is Core's and every gem's "Magic.Gem".
    /// </summary>
    [Browsable(false)]
    public IReadOnlyList<string> Meters { get; set; } = ["Magic"];
}
