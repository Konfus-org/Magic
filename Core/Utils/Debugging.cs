using Magic.Interfaces;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Magic.Utils;

public static class Debugging
{
    private static readonly Lock _lock = new();
    private static readonly List<ILogger> _loggers = [];
    private static readonly List<(LogLevel Level, string Message, string File, int Line)> _queuedLogs = [];
    private static int _errors;

    /// <summary>Messages below this level are dropped before any logger sees them. The command line sets it.</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>How many messages at <see cref="LogLevel.Error"/> or above were written, for the exit code to report.</summary>
    public static int Errors => Volatile.Read(ref _errors);

    public static void LogDebug(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Debug, message, file, line);
    }

    public static void LogInfo(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Information, message, file, line);
    }

    public static void LogWarning(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Warning, message, file, line);
    }

    public static void LogError(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Error, message, file, line);
        Flush(); // Always flush on error so it is written even if the application crashes right after.
    }

    public static void Assert(bool condition, string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition)
        {
            Write(LogLevel.Critical, message, file, line);
            Flush();// Always flush on crit so it is written even if the application crashes right after.
            Debug.Assert(condition, message);
        }
    }

    /// <summary>
    /// Writes every message queued while no logger was registered, then flushes all loggers.
    /// </summary>
    internal static void Flush()
    {
        lock (_lock)
        {
            // We are crashing a crit ALWAYS means a crash
            if (_queuedLogs.Any(l => l.Level == LogLevel.Critical))
            {
                StringBuilder sb = new();
                foreach ((LogLevel Level, string Message, string File, int Line) log in _queuedLogs)
                {
                    string line = $"[{log.File}-{log.Line}][{log.Level}]: {log.Message}";
                    if (_loggers.Count == 0)
                    {
                        if (log.Level > LogLevel.Warning) Console.Error.WriteLine(line);
                        else Console.WriteLine(line);
                    }
                    sb.AppendLine(line);
                }
                File.WriteAllText($"{DateTime.Today.ToLocalTime():yyyy-MM-dd}.crash", sb.ToString());
            }

            // Log using registered loggers and clear the queue
            if (_loggers.Count > 0)
            {
                foreach ((LogLevel level, string message, string file, int line) in _queuedLogs)
                    foreach (ILogger logger in _loggers)
                        logger.Log(level, message, file, line);
                _queuedLogs.Clear();
                foreach (ILogger logger in _loggers)
                    logger.Flush();
            }
        }
    }

    internal static void RegisterLogger(ILogger logger)
    {
        lock (_lock)
        {
            _loggers.Add(logger);
        }
        Flush();
    }

    internal static void UnregisterLogger(ILogger logger)
    {
        lock (_lock)
        {
            _loggers.Remove(logger);
        }
    }

    private static void Write(LogLevel level, string message, string file, int line)
    {
        if (level < MinimumLevel)
            return;
        if (level >= LogLevel.Error)
            Interlocked.Increment(ref _errors);

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
