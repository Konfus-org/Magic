using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Magic.State;

namespace Magic.Debugging;

/// <summary>
/// Provides static methods and utilities for logging messages at various severity levels and managing log sinks.
/// </summary>
public static class Log
{
    private const int StartupQueueSize = 10_000;
    private static readonly List<ILogSink> _sinks = [];

    /// <summary>
    /// The lines printed before any sink was added, for the first one; null from then on.
    /// </summary>
    private static Queue<Line>? _queuedLinesToLog = new();
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
    }

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

    /// <summary>
    /// Sends every line logged from now on to <paramref name="sink"/> too, until the subscription is disposed, which
    /// waits until the sink has been given every line logged before, so it can be closed straight after; never
    /// disposed from inside the sink's own <see cref="ILogSink.Write"/>. The first sink added gets the lines logged
    /// before it first.
    /// </summary>
    internal static Subscription Register(ILogSink sink)
    {
        _sinks.Add(sink);

        // If we had any lines queued before there was a sink, give them to the first one now and drop the queue.
        if (_queuedLinesToLog is { } queue)
        {
            _queuedLinesToLog = null;
            foreach (Line line in queue)
                WriteSinks(line);
        }

        return new Subscription(() => Unregister(sink));
    }

    /// <summary>
    /// The lines logged before any sink was added, which no sink has printed; empty once one has.
    /// </summary>
    internal static IReadOnlyCollection<Line> GetUnprinted() => _queuedLinesToLog ?? (IReadOnlyCollection<Line>)[];

    private static void Unregister(ILogSink sink) => _sinks.Remove(sink);

    private static Line CreateLine(LoggingLevel level, string message, string member, string file, int line) =>
        new(DateTime.Now, level, message, Environment.CurrentManagedThreadId, file, line, member);

    private static void Write(LoggingLevel level, string message, string member, string file, int line)
    {
        Line lineStruct = CreateLine(level, message, member, file, line);
        // If there is a queue, we are before the first sink was added: queue the line and drop the oldest if full.
        if (_queuedLinesToLog is { } queue)
        {
            if (queue.Count == StartupQueueSize)
                queue.Dequeue();
            queue.Enqueue(lineStruct);
        }
        else
            WriteSinks(lineStruct);
    }

    /// <summary>
    /// Gives <paramref name="line"/> to every sink. A sink that throws is logged and dropped; the log goes on.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a sink throws, it is logged and the sink is dropped; the log goes on."
    )]
    private static void WriteSinks(in Line line)
    {
        for (int i = 0; i < _sinks.Count; i++)
        {
            try
            {
                _sinks[i].Write(line);
            }
            catch (Exception ex)
            {
                Error($"Log sink {_sinks[i].GetType().Name} threw and was removed: {ex}");
                _sinks.RemoveAt(i);
                i--;
            }
        }
    }

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

    /// <summary>
    /// A line in the log, or a change to the sinks list.
    /// </summary>
    private readonly record struct Entry(Line Line, Action? Change);
}
