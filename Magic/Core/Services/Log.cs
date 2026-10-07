using Magic.Interfaces;
using System.Runtime.CompilerServices;
using System.Text;

#pragma warning disable RS0030 // works before any service exists: a crash is written with System.IO and the console directly

namespace Magic.Utils;

/// <summary>
/// The engine's log: every message goes to each <see cref="ILogger"/> the loaded gems provide, and is kept until the
/// first of them is there (the last <see cref="MaxQueued"/>: a run without any, a test, keeps no more). A critical
/// message is also written into today's <c>.crash</c> file at once.
/// </summary>
public static class Log
{
    /// <summary>
    /// The most messages kept while no logger is there; past it the oldest go.
    /// </summary>
    private const int MaxQueued = 10_000;

    private static readonly Lock _lock = new();
    private static readonly List<ILogger> _loggers = [];
    private static readonly List<(LogLevel Level, string Message, string File, int Line)> _queued = [];

    /// <summary>
    /// Messages below this level are dropped before any logger sees them. The command line sets it.
    /// </summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// Whether <see cref="LogLevel.Verbose"/> messages are written. <c>--verbose</c> sets it.
    /// </summary>
    public static bool EnableVerbose { get; set; }

    /// <summary>
    /// How many messages at <see cref="LogLevel.Error"/> or above were written, for the exit code to report.
    /// </summary>
    public static int Errors { get => Volatile.Read(ref field); private set; }

    /// <summary>
    /// A broken promise: written as critical (a crash file) when <paramref name="condition"/> is false.
    /// </summary>
    public static void Assert(bool condition, string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (condition)
            return;

        Write(LogLevel.Critical, message, file, line);
        System.Diagnostics.Debug.Assert(condition, message);
    }

    /// <summary>
    /// For periodic or high-volume diagnostics (FPS, streaming); dropped unless <see cref="EnableVerbose"/>.
    /// </summary>
    public static void Verbose(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Verbose, message, file, line);
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
    }

    /// <summary>
    /// Writes at <paramref name="level"/> as if from <paramref name="file"/> and <paramref name="line"/>: for a helper
    /// that logs on its caller's behalf.
    /// </summary>
    public static void Write(LogLevel level, string message, string file, int line)
    {
        if (level == LogLevel.Verbose ? !EnableVerbose : level < MinimumLevel)
            return;

        lock (_lock)
        {
            if (level >= LogLevel.Error)
                Errors++;

            // Nothing to write to yet (loggers come from gems): keep the message until one registers.
            if (_loggers.Count == 0)
            {
                if (_queued.Count == MaxQueued)
                    _queued.RemoveAt(0);

                _queued.Add((level, message, file, line));
            }
            else
            {
                foreach (ILogger logger in _loggers)
                    logger.Log(level, message, file, line);
            }

            // A critical message means a crash: it is written beside the logs at once, with whatever was waiting.
            if (level == LogLevel.Critical)
                WriteCrash(_loggers.Count == 0 ? _queued : [(level, message, file, line)]);
        }
    }

    /// <summary>
    /// Adds a logger: the loader adds every gem that is one as it loads, and nothing else does. It is handed what was
    /// written before any logger was there.
    /// </summary>
    internal static void Register(ILogger logger)
    {
        lock (_lock)
        {
            _loggers.Add(logger);

            // What was written before any logger was there goes to the first.
            foreach ((LogLevel level, string message, string file, int line) in _queued)
                logger.Log(level, message, file, line);

            _queued.Clear();
        }
    }

    /// <summary>
    /// Takes a logger out, as the loader does when its gem unloads.
    /// </summary>
    internal static void Unregister(ILogger logger)
    {
        lock (_lock)
            _loggers.Remove(logger);
    }

    /// <summary>
    /// The messages into today's <c>.crash</c> file beside the log files, never in whatever folder the host was
    /// started from; to the console too when no logger shows them. Written with System.IO directly, the one place
    /// that is allowed: a crash can come before any service exists. Under the lock.
    /// </summary>
    private static void WriteCrash(List<(LogLevel Level, string Message, string File, int Line)> logs)
    {
        StringBuilder text = new();
        foreach ((LogLevel level, string message, string file, int line) in logs)
        {
            string written = $"[{file}-{line}][{level}]: {message}";
            if (_loggers.Count == 0)
            {
                if (level > LogLevel.Warning)
                    Console.Error.WriteLine(written);
                else
                    Console.WriteLine(written);
            }

            text.AppendLine(written);
        }

        Directory.CreateDirectory(Services.Project.Logs);
        File.AppendAllText(Path.Combine(Services.Project.Logs, $"{DateTime.Today:yyyy-MM-dd}.crash"), text.ToString());
    }
}
