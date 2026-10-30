using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Magic.Utils;

namespace Magic.Debugging;

/// <summary>
/// Provides static methods and utilities for logging messages at various severity levels and managing log sinks.
/// </summary>
public static class Log
{
    private const int HistorySize = 10_000;

    /// <summary>
    /// Guards the sinks and the history, so a sink is given each line once, in order, and never two at a time.
    /// </summary>
    private static readonly Lock _lock = new();
    private static readonly List<ILogSink> _sinks = [];

    /// <summary>
    /// The last lines logged, which every sink added is given first.
    /// </summary>
    private static readonly Queue<Line> _history = new();
    private static int _errorCount;

    public static VerbosityLevel Verbosity
    {
        get;
        set
        {
            // Always log the verbosity change, even if it is the same as before, so that the log shows what was requested.
            field =
                value;
            Write(LoggingLevel.Info, $"Verbosity set to {value}", nameof(Verbosity), string.Empty, 0);
        }
    } = VerbosityLevel.Normal;

    /// <summary>
    /// How many errors have been logged; under --fail-on-error any at all make the exit code 2.
    /// </summary>
    public static int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>
    /// Detail that is usually noise: logged only under --verbose, and otherwise not even queued.
    /// </summary>
    public static void Verbose(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (Verbosity == VerbosityLevel.Detailed)
            Write(LoggingLevel.Verbose, message, member, file, line);
    }

    public static void Info(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (Verbosity >= VerbosityLevel.Normal)
            Write(LoggingLevel.Info, message, member, file, line);
    }

    /// <summary>
    /// Something works, only worse.
    /// </summary>
    public static void Warn(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (Verbosity >= VerbosityLevel.Minimal)
            Write(LoggingLevel.Warn, message, member, file, line);
    }

    /// <summary>
    /// Something failed: what it was is skipped or stood in for, and the frame goes on.
    /// </summary>
    public static void Error(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (Verbosity < VerbosityLevel.Minimal)
            return;

        Interlocked.Increment(ref _errorCount);
        Write(LoggingLevel.Error, message, member, file, line);
    }

    /// <summary>
    /// Something failed that the run cannot recover from; the caller decides what happens next. Logged at any
    /// verbosity, and counted as an error.
    /// </summary>
    public static void Critical(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        Interlocked.Increment(ref _errorCount);
        Write(LoggingLevel.Critical, message, member, file, line);
    }

    /// <summary>
    /// Has every sink print what it was given so far. Called at exit, and before a crash.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a sink throws, it is logged and the sink is dropped; the log goes on."
    )]
    public static void Flush()
    {
        lock (_lock)
        {
            for (int i = _sinks.Count - 1; i >= 0; i--)
            {
                try
                {
                    _sinks[i].Flush();
                }
                catch (Exception ex)
                {
                    ILogSink sink = _sinks[i];
                    _sinks.RemoveAt(i);
                    Error($"Log sink {sink.GetType().Name} threw and was removed: {ex}");
                }
            }
        }
    }

    /// <summary>
    /// Sends every line logged from now on to <paramref name="sink"/> too, until the subscription is disposed, which
    /// waits until the sink has been given every line logged before, so it can be closed straight after; never
    /// disposed from inside the sink's own <see cref="ILogSink.Write"/>. Every sink added is given the last lines
    /// logged first.
    /// </summary>
    public static Subscription Register(ILogSink sink)
    {
        lock (_lock)
        {
            _sinks.Add(sink);

            // A copy, since a sink that throws logs an error, which adds to the history.
            foreach (Line line in _history.ToArray())
            {
                if (!Write(line, sink))
                    break;
            }
        }

        return new Subscription(() => Unregister(sink));
    }

    /// <summary>
    /// The last lines logged, oldest first.
    /// </summary>
    internal static Line[] GetHistory()
    {
        lock (_lock)
            return [.. _history];
    }

    private static void Unregister(ILogSink sink)
    {
        lock (_lock)
            _sinks.Remove(sink);
    }

    private static Line CreateLine(LoggingLevel level, string message, string member, string file, int line) =>
        new(DateTime.Now, level, message, Environment.CurrentManagedThreadId, file, line, member);

    [SuppressMessage(
        "ApiDesign",
        "RS0030:Do not use banned APIs",
        Justification = "We always write to the console as our default log sink."
    )]
    private static void Write(LoggingLevel level, string message, string member, string file, int line)
    {
        Line lineStruct = CreateLine(level, message, member, file, line);
        lock (_lock)
        {
            if (_history.Count == HistorySize)
                _history.Dequeue();
            _history.Enqueue(lineStruct);
            WriteSinks(lineStruct);
        }

        // Log always writes to console
        string text = lineStruct.ToString();
        if (level >= LoggingLevel.Error)
            Console.Error.WriteLine(Console.IsErrorRedirected ? text : Colorize(level, text));
        else
            Console.WriteLine(Console.IsOutputRedirected ? text : Colorize(level, text));
    }

    /// <summary>
    /// Gives <paramref name="line"/> to every sink. A sink that throws is logged and dropped; the log goes on.
    /// </summary>
    private static void WriteSinks(in Line line)
    {
        foreach (var sink in _sinks.ToArray())
        {
            Write(line, sink);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a sink throws, it is logged and the sink is dropped; the log goes on."
    )]
    private static bool Write(Line line, ILogSink sink)
    {
        try
        {
            sink.Write(line);
            return true;
        }
        catch (Exception ex)
        {
            // Removed before the error is logged, so the sink isn't given the line about itself.
            _sinks.Remove(sink);
            Error($"Log sink {sink.GetType().Name} threw and was removed: {ex}");
            return false;
        }
    }

    private static string Colorize(LoggingLevel level, string text) =>
        level switch
        {
            LoggingLevel.Verbose => $"\e[90m{text}\e[0m", // gray
            LoggingLevel.Info => text, // default
            LoggingLevel.Warn => $"\e[33m{text}\e[0m", // yellow
            LoggingLevel.Error => $"\e[31m{text}\e[0m", // red
            LoggingLevel.Critical => $"\e[1;97;41m{text}\e[0m", // bold white on red
            _ => text,
        };

    public enum VerbosityLevel
    {
        Quiet,
        Minimal,
        Normal,
        Detailed,
        Diagnostic,
    }

    public enum LoggingLevel
    {
        Verbose,
        Info,
        Warn,
        Error,
        Critical,
    }

    public readonly record struct Line(
        DateTime Timestamp,
        LoggingLevel Level,
        string Message,
        int ThreadId,
        string CallerFile,
        int CallerFileLineNumber,
        string CallerMemberName
    )
    {
        public override string ToString() =>
            $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [thread {ThreadId}] {Message} ({Path.GetFileName(CallerFile)}:{CallerFileLineNumber} in {CallerMemberName})";
    }
}
