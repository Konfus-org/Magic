using Magic.Interfaces;

namespace GemName;

/// <summary>
/// How GemName runs. Each property's initial value is its default, and its doc comment is its description where the
/// settings are shown; a project's preset sets them, or <c>--set GemName.intervalSec=5</c> on Magic's command line.
/// </summary>
internal sealed class GemNameSettings : ISettings
{
    /// <summary>
    /// How often the gem says it is running, in seconds.
    /// </summary>
    public double IntervalSec { get; set; } = 10;
}
