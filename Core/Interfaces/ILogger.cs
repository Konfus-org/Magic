namespace Magic.Interfaces;

public enum LogLevel
{
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
