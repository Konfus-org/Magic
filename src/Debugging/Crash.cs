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
internal static class Crash
{
    private static int _crashing;

    /// <summary>
    /// Logs <paramref name="message"/> as <see cref="Log.Critical"/>, writes it, the stack it was called from and the
    /// lines no sink has printed to Logs/&lt;time&gt;.crash beside Magic, tells the terminal where, breaks an attached
    /// debugger and ends the process. Nothing else runs: no gem is disposed.
    /// </summary>
    [DoesNotReturn]
    [SuppressMessage(
        "ApiDesign",
        "RS0030:Do not use banned APIs",
        Justification = "A crash prints straight to the terminal, past sinks that may be what failed."
    )]
    internal static void Now(
        string message,
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

        DateTime time = DateTime.Now;
        string where = $"{Path.GetFileName(file)}:{line} in {member}";

        // Skips Now, so the stack starts where the crash was called.
        StringBuilder text = new();
        text.Append("Crash: ")
            .Append(message)
            .Append(" (")
            .Append(where)
            .Append(")\n")
            .Append(new StackTrace(1, true).ToString());

        IReadOnlyCollection<Log.Line> unprinted = Log.GetUnprinted();
        if (unprinted.Count > 0)
        {
            text.Append("\nLines no sink printed:\n");
            foreach (Log.Line queued in unprinted)
                text.Append(queued.ToString()).Append('\n');
        }

        string crashFile = WriteCrashFile(time, text.ToString().ReplaceLineEndings("\n"));

        Console.Error.WriteLine($"Crash: {message} ({where})");
        Console.Error.WriteLine(crashFile);

        // A developer debugging any build stops here, at the crash, before the process ends. With no debugger
        // attached nothing waits, so a run nobody watches still ends.
        if (Debugger.IsAttached)
            Debugger.Break();

        Environment.FailFast(message);
    }

    /// <summary>
    /// Writes <paramref name="text"/> to Logs/&lt;time&gt;.crash beside Magic, and says where it went or why it could
    /// not.
    /// </summary>
    [SuppressMessage(
        "ApiDesign",
        "RS0030:Do not use banned APIs",
        Justification = "A crash can come before any file system exists."
    )]
    private static string WriteCrashFile(DateTime time, string text)
    {
        string folder = Path.Join(AppContext.BaseDirectory, "Logs");
        string path = Path.Join(
            folder,
            time.ToString("yyyy-MM-dd_HH-mm-ss.fff", CultureInfo.InvariantCulture) + ".crash"
        );
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
}
