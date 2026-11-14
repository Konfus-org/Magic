using System.ComponentModel;
using Magic.Configuration;

namespace Magic.GemName;

/// <summary>
/// How often the gem logs that it runs.
/// </summary>
[Category("GemName")]
public sealed class GemNameSettings : ISettings
{
    /// <summary>
    /// The number of frames between two log lines that say the gem runs. 0 stops the log lines.
    /// </summary>
    public uint HeartbeatFrames { get; set; } = 60;
}
