namespace Magic.Interfaces;

/// <summary>
/// How much a log message matters, least first. <see cref="Utils.Log.MinimumLevel"/> drops the ones below it;
/// <see cref="Verbose"/> goes by <c>--verbose</c> instead.
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// Periodic and high-volume diagnostics (FPS, streaming). Written only with --verbose, whatever the minimum level.
    /// </summary>
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Where the log goes: a gem that is one is handed every message <see cref="Utils.Log"/> writes, from any thread but
/// one at a time; the first to load also gets what was written before it.
/// </summary>
public interface ILogger
{
    void Log(LogLevel level, string message, string file, int line);
}
