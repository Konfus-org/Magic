using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Magic.Projects;
using Magic.Utils;

namespace Magic.Debugging;

/// <summary>
/// Logs from any thread to the terminal and to each registered sink.
/// </summary>
/// <remarks>
/// The last lines are kept, so a sink that registers late and a crash file get them too.
/// </remarks>
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
            // Log the verbosity change even when it is the same, so that the log shows what was requested.
            field =
                value;
            Write(LoggingLevel.Info, $"Verbosity set to {value}", nameof(Verbosity), string.Empty, 0, false, null);
        }
    } = VerbosityLevel.Normal;

    /// <summary>
    /// How many errors were logged. With --fail-on-error, one or more makes the exit code 2.
    /// </summary>
    public static int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>
    /// Detail that is usually noise: logged only at detailed verbosity or above, and otherwise not even queued.
    /// </summary>
    public static void Verbose(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (Verbosity >= VerbosityLevel.Detailed)
            Write(LoggingLevel.Verbose, message, member, file, line, false, null);
    }

    /// <summary>
    /// What happened, as it goes. With <paramref name="onScreen"/>, the debug UI shows it on screen too, for
    /// <paramref name="showForSec"/> seconds, or for the time its settings give when that is null.
    /// </summary>
    public static void Info(
        string message,
        bool onScreen = false,
        float? showForSec = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (Verbosity >= VerbosityLevel.Normal)
            Write(LoggingLevel.Info, message, member, file, line, onScreen, showForSec);
    }

    /// <summary>
    /// Something works, only worse. With <paramref name="onScreen"/>, the debug UI shows it on screen too, for
    /// <paramref name="showForSec"/> seconds, or for the time its settings give when that is null.
    /// </summary>
    public static void Warn(
        string message,
        bool onScreen = false,
        float? showForSec = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (Verbosity >= VerbosityLevel.Minimal)
            Write(LoggingLevel.Warn, message, member, file, line, onScreen, showForSec);
    }

    /// <summary>
    /// Something failed, and is skipped or replaced, and the frame goes on. Counted as an error at any verbosity.
    /// </summary>
    /// <remarks>
    /// With <paramref name="onScreen"/>, the debug UI shows it on screen too, for <paramref name="showForSec"/>
    /// seconds, or for the time its settings give when that is null.
    /// </remarks>
    public static void Error(
        string message,
        bool onScreen = false,
        float? showForSec = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        Interlocked.Increment(ref _errorCount);
        if (Verbosity >= VerbosityLevel.Minimal)
            Write(LoggingLevel.Error, message, member, file, line, onScreen, showForSec);
    }

    /// <summary>
    /// Something failed that the run cannot recover from; the caller decides what happens next. Counted as an error.
    /// </summary>
    public static void Critical(
        string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        Interlocked.Increment(ref _errorCount);
        Write(LoggingLevel.Critical, message, member, file, line, false, null);
    }

    /// <summary>
    /// Makes each sink write what it was given so far. Core calls it at exit and before a crash.
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
                    Error($"The log sink of {GemRegistry.GetGemName(sink.GetType())} threw and was removed: {ex}");
                }
            }
        }
    }

    /// <summary>
    /// Sends each line logged from now on to <paramref name="sink"/>, until the subscription is disposed. The last
    /// lines logged go first.
    /// </summary>
    /// <remarks>
    /// Disposal waits until the sink has each line logged before, so the sink can close after it. Do not dispose the
    /// subscription inside the sink's <see cref="ILogSink.Write"/>.
    /// </remarks>
    public static Subscription Register(ILogSink sink)
    {
        lock (_lock)
        {
            _sinks.Add(sink);

            // Copy, as a sink that throws logs an error, which adds to the history.
            foreach (Line line in _history.ToArray())
            {
                if (!Write(line, sink))
                    break;
            }
        }

        Info($"Log sink added: {GemRegistry.GetGemName(sink.GetType())}.");

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

    private static void Write(
        LoggingLevel level,
        string message,
        string member,
        string file,
        int line,
        bool onScreen,
        float? showForSec
    )
    {
        Line logged = new(
            DateTime.Now,
            level,
            message,
            Environment.CurrentManagedThreadId,
            file,
            line,
            member,
            onScreen,
            showForSec
        );
        lock (_lock)
        {
            if (_history.Count == HistorySize)
                _history.Dequeue();
            _history.Enqueue(logged);

            // Copy, as a sink that throws is removed and logs an error.
            foreach (ILogSink sink in _sinks.ToArray())
            {
                Write(logged, sink);
            }
        }

        Terminal.Write(logged);
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
            // Remove it before the error is logged, so the sink does not get the line about itself.
            _sinks.Remove(sink);
            Error($"The log sink of {GemRegistry.GetGemName(sink.GetType())} threw and was removed: {ex}");
            return false;
        }
    }

    public enum VerbosityLevel
    {
        Quiet,
        Minimal,
        Normal,
        Detailed,
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
        string CallerMemberName,
        bool IsOnScreen,
        float? ShowForSec
    )
    {
        public override string ToString() =>
            $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [thread {ThreadId}] {Message} "
            + $"({Path.GetFileName(CallerFile)}:{CallerFileLineNumber} in {CallerMemberName})";
    }
}
