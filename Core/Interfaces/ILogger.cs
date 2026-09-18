namespace Magic.Interfaces;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public interface ILogger
{
    void Log(LogLevel level, string message, string file, int line);
    void Flush();
}
