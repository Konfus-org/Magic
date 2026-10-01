namespace Magic.Interfaces;

public enum LogLevel
{
    /// <summary>Periodic and high-volume diagnostics (FPS, streaming). Written only with --verbose, whatever the minimum level.</summary>
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public interface ILogger
{
    void Log(LogLevel level, string message, string file, int line);

    void Flush();
}
