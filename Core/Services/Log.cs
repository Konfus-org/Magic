using Core.Interfaces;
using System.Runtime.CompilerServices;

namespace Core.Services;

public static class Log
{
    private static readonly List<ILogger> _loggers = [];
    private static readonly List<(LogLevel Level, string Message, string File, int Line)> _queuedLogs = [];

    internal static void Flush()
    {
        if (_loggers.Count == 0)
            return;

        for (int i = 0; i < _queuedLogs.Count; i++)
        {
            (LogLevel level, string? message, string? file, int line) = _queuedLogs[i];
            foreach (ILogger logger in _loggers)
                logger.Log(level, message, file, line);
        }

        foreach (ILogger logger in _loggers)
        {
            logger.Flush();
        }
    }

    internal static void Register(ILogger logger)
    {
        _loggers.Add(logger);
    }

    public static void Debug(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (_loggers.Count == 0)
        {
            _queuedLogs.Add((LogLevel.Debug, message, file, line));
            return;
        }

        foreach (ILogger logger in _loggers)
        {
            logger.Log(LogLevel.Debug, message, file, line);
        }
    }

    public static void Info(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (_loggers.Count == 0)
        {
            _queuedLogs.Add((LogLevel.Debug, message, file, line));
            return;
        }

        foreach (ILogger logger in _loggers)
        {
            logger.Log(LogLevel.Information, message, file, line);
        }
    }

    public static void Warn(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (_loggers.Count == 0)
        {
            _queuedLogs.Add((LogLevel.Debug, message, file, line));
            return;
        }

        foreach (ILogger logger in _loggers)
        {
            logger.Log(LogLevel.Warning, message, file, line);
        }
    }

    public static void Error(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (_loggers.Count == 0)
        {
            _queuedLogs.Add((LogLevel.Debug, message, file, line));
            return;
        }

        foreach (ILogger logger in _loggers)
        {
            logger.Log(LogLevel.Error, message, file, line);
        }

        // We always flush on error to ensure that the error is logged immediately, even if the application crashes or exits unexpectedly.
        Flush();
    }
}
