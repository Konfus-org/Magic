using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Magic.Debugging;

/// <summary>
/// Ends the process at once, from any thread, leaving a crash file and the OS's crash report.
/// </summary>
public static class Assert
{
    private static int _crashing;

    /// <summary>
    /// Crashes the run <see cref="Now"/> when <paramref name="condition"/> is false, a state that cannot be. Active in
    /// each build.
    /// </summary>
    /// <remarks>
    /// The message is the condition's source, then <paramref name="message"/>.
    /// </remarks>
    public static void True(
        [DoesNotReturnIf(false)] bool condition,
        string message = "",
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (!condition)
            Now(FormatFailed(expression, message), member, file, line);
    }

    /// <summary>
    /// Crashes the run <see cref="Now"/> when <paramref name="condition"/> is true, a state that cannot be. Active in
    /// each build.
    /// </summary>
    /// <remarks>
    /// The message is the condition's source, then <paramref name="message"/>.
    /// </remarks>
    public static void False(
        [DoesNotReturnIf(true)] bool condition,
        string message = "",
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        if (condition)
            Now(FormatFailed(expression, message), member, file, line);
    }

    /// <summary>
    /// Logs <paramref name="message"/> as <see cref="Log.Critical"/>, writes a crash file and ends the process. No gem
    /// is disposed.
    /// </summary>
    /// <remarks>
    /// The crash file is Logs/&lt;time&gt;.crash beside Magic, with the message, the calling stack and the last lines
    /// logged. The terminal shows its path. An attached debugger breaks first.
    /// </remarks>
    [DoesNotReturn]
    [SuppressMessage(
        "ApiDesign",
        "RS0030:Do not use banned APIs",
        Justification = "A crash prints straight to the terminal, past sinks that may be what failed, and can come "
            + "before any file system exists."
    )]
    public static void Now(
        string message = "",
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0
    )
    {
        // Only the first crash writes; another thread crashing at the same time waits for the process to end.
        if (Interlocked.Exchange(ref _crashing, 1) == 1)
            Thread.Sleep(Timeout.Infinite);

        Log.Critical(message, member, file, line);
        Log.Flush();

        string where = $"{Path.GetFileName(file)}:{line} in {member}";

        // Skips Now, so the stack starts where the crash was called.
        StringBuilder text = new();
        text.Append("Crash: ")
            .Append(message)
            .Append(" (")
            .Append(where)
            .Append(")\n")
            .Append(new StackTrace(1, true).ToString());

        Log.Line[] history = Log.GetHistory();
        if (history.Length > 0)
        {
            text.Append("\nLast lines logged:\n");
            foreach (Log.Line logged in history)
                text.Append(logged.ToString()).Append('\n');
        }

        string folder = Path.Join(AppContext.BaseDirectory, "Logs");
        string path = Path.Join(
            folder,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss.fff", CultureInfo.InvariantCulture) + ".crash"
        );
        string crashFile;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, text.ToString().ReplaceLineEndings("\n"));
            crashFile = $"Crash file: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            crashFile = $"The crash file {path} could not be written: {ex.Message}";
        }

        Console.Error.WriteLine($"Crash: {message} ({where})");
        Console.Error.WriteLine(crashFile);

        // A developer debugging any build stops here, at the crash, before the process ends. With no debugger
        // attached nothing waits, so a run nobody watches still ends.
        if (Debugger.IsAttached)
            Debugger.Break();

        Environment.FailFast(message);
    }

    private static string FormatFailed(string expression, string message) =>
        message.Length == 0 ? $"Assert failed: {expression}" : $"Assert failed: {expression}: {message}";
}
