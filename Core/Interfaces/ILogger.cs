using System.Runtime.CompilerServices;

namespace Core.Interfaces;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public interface ILogger
{
    void Log(LogLevel level, string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);
    void Flush();
}
