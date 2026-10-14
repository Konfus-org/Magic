using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Magic.Contexts.Logging;
using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// The engine's log. Any thread writes to a channel and one background task prints, so logging never waits on the
/// terminal. Static, so that it works before any gem exists and after one has broken. The compiler fills in where
/// each line was logged from, and the log adds the thread and frame (<see cref="LogLine"/>).
/// <para>
/// <see cref="Verbose(string, string, string, int)"/> lines are logged only under --verbose. Lines go to the
/// terminal, then to every gem that is an <see cref="ILogSink"/>. <see cref="Critical"/>
/// and a failed <see cref="Assert(bool, string, string, string, string, int)"/> end the process at once, leaving what
/// the terminal had not printed yet in a .crash file in Logs/ beside Magic.
/// </para>
/// </summary>
public static class Debugging
{
    /// <summary>
    /// How long removing a sink waits for it to be given the lines logged before, so a stuck sink cannot hang the
    /// run's end.
    /// </summary>
    private const int CatchUpSeconds = 5;

    // Not single reader: Critical takes what the printing task has not.
    private static readonly Channel<Entry> _entries = Channel.CreateUnbounded<Entry>();

    // Only the printing task touches it, in order with the lines.
    private static readonly List<ILogSink> _sinks = [];
    private static readonly Task _printing = PrintAsync();
    private static int _errorCount;
    private static int _crashing;

    // The frame index + 1, so 0 is outside the frame loop: a ulong? cannot be read and written atomically.
    private static ulong _frameStamp;

    /// <summary>
    /// How many errors have been logged; under --fail-on-error any at all make the exit code 2.
    /// </summary>
    internal static int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>
    /// The frame the main thread is in, stamped on every line; null outside the frame loop, which sets it.
    /// </summary>
    internal static ulong? Frame
    {
        get
        {
            ulong stamp = Volatile.Read(ref _frameStamp);
            return stamp == 0 ? null : stamp - 1;
        }

        set => Volatile.Write(ref _frameStamp, value is { } index ? index + 1 : 0);
    }

    /// <summary>
    /// Whether <see cref="Verbose(string, string, string, int)"/> lines are logged: --verbose, set once before the
    /// engine runs.
    /// </summary>
    internal static bool IsVerbose { get; set; }

    /// <summary>
    /// Detail that is usually noise: logged only under --verbose, and otherwise not even queued.
    /// </summary>
    public static void Verbose(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (IsVerbose)
        {
            Write(LogLevel.Verbose, message, member, file, line);
        }
    }

    public static void Info(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Info, message, member, file, line);
    }

    /// <summary>
    /// Something works, only worse.
    /// </summary>
    public static void Warn(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Write(LogLevel.Warn, message, member, file, line);
    }

    /// <summary>
    /// Something failed: what it was is skipped or stood in for, and the frame goes on.
    /// </summary>
    public static void Error(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Interlocked.Increment(ref _errorCount);
        Write(LogLevel.Error, message, member, file, line);
    }

    /// <summary>
    /// The run cannot go on: the process ends here, from any thread. The lines the terminal has not printed yet, this
    /// one and the stack it was called from go to a .crash file in Logs/ beside Magic; the terminal is told where; and
    /// the OS records the crash (a dump, and an attached debugger breaks). Nothing else runs: no gem is disposed and
    /// no sink sees the line.
    /// </summary>
    [DoesNotReturn]
    public static void Critical(string message, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Crash(Stamp(LogLevel.Critical, message, member, file, line));
    }

    /// <summary>
    /// State that cannot be: when <paramref name="condition"/> is false, the run is <see cref="Critical"/>, with the
    /// condition's source as the message, and <paramref name="message"/> after it. On in every build.
    /// </summary>
    public static void Assert([DoesNotReturnIf(false)] bool condition, string message = "", [CallerArgumentExpression(nameof(condition))] string expression = "", [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition)
        {
            Crash(Stamp(LogLevel.Critical, Failed(expression, message), member, file, line));
        }
    }

    /// <summary>
    /// Sends every line logged from now on to <paramref name="sink"/> too, until it is removed.
    /// </summary>
    internal static void Add(ILogSink sink)
    {
        _entries.Writer.TryWrite(new Entry(default, () => _sinks.Add(sink)));
    }

    /// <summary>
    /// Stops sending lines to <paramref name="sink"/>. Waits until it has been given every line logged before, so it
    /// can be closed straight after; never called from inside the sink's own <see cref="ILogSink.Write"/>.
    /// </summary>
    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits", Justification = "Removing a sink is synchronous, and the printing task never needs the waiting thread, so the wait cannot deadlock; it is bounded besides.")]
    internal static void Remove(ILogSink sink)
    {
        TaskCompletionSource removed = new();

        // The printing task has finished, at exit: the sink gets nothing more.
        if (!_entries.Writer.TryWrite(new Entry(default, RemoveSink)))
        {
            return;
        }

        if (!removed.Task.Wait(TimeSpan.FromSeconds(CatchUpSeconds)))
        {
            Warn($"Log sink {sink.GetType().Name} did not catch up within {CatchUpSeconds} s, so its last lines may be lost.");
        }

        void RemoveSink()
        {
            _sinks.Remove(sink);
            removed.SetResult();
        }
    }

    /// <summary>
    /// Prints everything written so far and stops: lines written after it are dropped. Called once, at exit.
    /// </summary>
    internal static async Task FlushAsync()
    {
        _entries.Writer.TryComplete();
        await _printing.ConfigureAwait(false);
    }

    private static LogLine Stamp(LogLevel level, string message, string member, string file, int line)
    {
        return new(DateTime.Now, level, message, Frame, Environment.CurrentManagedThreadId, file, line, member);
    }

    private static void Write(LogLevel level, string message, string member, string file, int line)
    {
        _entries.Writer.TryWrite(new Entry(Stamp(level, message, member, file, line), null));
    }

    private static string Failed(string expression, string message)
    {
        return message.Length == 0 ? $"Assert failed: {expression}" : $"Assert failed: {expression}: {message}";
    }

    private static async Task PrintAsync()
    {
        ChannelReader<Entry> reader = _entries.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Once a crash begins, what is left belongs to the crash file, which so holds an unbroken run of lines.
            while (Volatile.Read(ref _crashing) == 0 && reader.TryRead(out Entry entry))
            {
                if (entry.Change is { } change)
                {
                    change();
                    continue;
                }

                TextWriter terminal = entry.Line.Level >= LogLevel.Error ? Console.Error : Console.Out;
                await terminal.WriteLineAsync(entry.Line.ToString()).ConfigureAwait(false);
                WriteSinks(entry.Line);
            }

            if (Volatile.Read(ref _crashing) != 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Gives <paramref name="line"/> to every sink. A sink that throws is logged and dropped; the log goes on.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a sink throws, it is logged and the sink is dropped; the log goes on.")]
    private static void WriteSinks(in LogLine line)
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

    /// <summary>
    /// Writes the crash file for <paramref name="critical"/>, tells the terminal and ends the process.
    /// </summary>
    [DoesNotReturn]
    private static void Crash(in LogLine critical)
    {
        // Only the first crash writes; another thread crashing at the same time waits for the process to end.
        if (Interlocked.Exchange(ref _crashing, 1) == 1)
        {
            Thread.Sleep(Timeout.Infinite);
        }

        // Nothing more is queued, and whatever the printing task has not taken yet goes to the crash file.
        _entries.Writer.TryComplete();
        StringBuilder text = new();
        while (_entries.Reader.TryRead(out Entry entry))
        {
            if (entry.Change is null)
            {
                text.Append(entry.Line.ToString()).Append('\n');
            }
        }

        text.Append(critical.ToString()).Append('\n').Append(new StackTrace(1, true).ToString());
        string written = WriteCrashFile(critical.Time, text.ToString().ReplaceLineEndings("\n"));

        Console.Error.WriteLine(critical.ToString());
        Console.Error.WriteLine(written);

        // A developer debugging any build stops here, at the crash, before the process ends. With no debugger
        // attached nothing waits, so a run nobody watches still ends.
        if (Debugger.IsAttached)
        {
            Debugger.Break();
        }

        Environment.FailFast(critical.Message);
    }

    /// <summary>
    /// Writes <paramref name="text"/> to Logs/&lt;time&gt;.crash beside Magic, and says where it went or why it could
    /// not.
    /// </summary>
    private static string WriteCrashFile(DateTime time, string text)
    {
        string folder = Path.Join(AppContext.BaseDirectory, "Logs");
        string path = Path.Join(folder, time.ToString("yyyy-MM-dd_HH-mm-ss.fff", CultureInfo.InvariantCulture) + ".crash");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, text);
            return $"Crash file: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The crash file {path} could not be written: {ex.Message}";
        }
    }

    /// <summary>
    /// A line in the log, or a change to the sinks list.
    /// </summary>
    private readonly record struct Entry(LogLine Line, Action? Change);
}
