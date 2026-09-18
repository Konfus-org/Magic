using Magic.Interfaces;
using System.Runtime.CompilerServices;

namespace Magic.Utils;

public static class Log
{
    private static readonly object _lock = new();
    private static readonly List<ILogger> _loggers = [];
    private static readonly List<(LogLevel Level, string Message, string File, int Line)> _queuedLogs = [];

    /// <summary>
    /// Writes every message queued while no logger was registered, then flushes all loggers.
    /// </summary>
    public static void Flush()
    {
        lock (_lock)
        {
            if (_loggers.Count == 0)
                return;

            foreach ((LogLevel level, string message, string file, int line) in _queuedLogs)
                foreach (ILogger logger in _loggers)
                    logger.Log(level, message, file, line);
            _queuedLogs.Clear();

            foreach (ILogger logger in _loggers)
                logger.Flush();
        }
    }

    public static void Register(ILogger logger)
    {
        lock (_lock)
        {
            _loggers.Add(logger);
        }
        Flush();
    }

    public static void Unregister(ILogger logger)
    {
        lock (_lock)
        {
            _loggers.Remove(logger);
        }
    }

    public static void Debug(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Debug, message, file, line);
    }

    public static void Info(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Information, message, file, line);
    }

    public static void Warn(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Warning, message, file, line);
    }

    public static void Error(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Error, message, file, line);
        // Always flush on error so it is written even if the application crashes right after.
        Flush();
    }

    private static void Write(LogLevel level, string message, string file, int line)
    {
        lock (_lock)
        {
            // Nothing to write to yet (loggers come from gems): keep the message until one registers.
            if (_loggers.Count == 0)
            {
                _queuedLogs.Add((level, message, file, line));
                return;
            }

            foreach (ILogger logger in _loggers)
                logger.Log(level, message, file, line);
        }
    }
}
