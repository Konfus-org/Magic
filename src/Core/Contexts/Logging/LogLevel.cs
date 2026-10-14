using Magic.Interfaces;

namespace Magic.Contexts.Logging;

/// <summary>
/// How bad a <see cref="LogLine"/> is.
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// Detail that is usually noise: logged only under --verbose.
    /// </summary>
    Verbose,

    Info,

    /// <summary>
    /// Something works, only worse.
    /// </summary>
    Warn,

    /// <summary>
    /// Something failed: what it was is skipped or stood in for, and the frame goes on.
    /// </summary>
    Error,

    /// <summary>
    /// The process is ending. Written to the terminal and the crash file only: no <see cref="ILogSink"/> sees it.
    /// </summary>
    Critical,
}
