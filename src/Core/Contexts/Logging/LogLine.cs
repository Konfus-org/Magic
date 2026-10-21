using System.Globalization;
using System.IO;
using Magic.Services;

namespace Magic.Contexts.Logging;

/// <summary>
/// One line of the log, with where and when it was written. Where comes from the compiler, at the call to
/// <see cref="Debugging"/>, so it costs nothing to run.
/// </summary>
/// <param name="Time">When it was logged, in local time.</param>
/// <param name="Level">How bad it is.</param>
/// <param name="Message">What happened.</param>
/// <param name="Frame">The index of the frame the main thread was in; null before the first frame and after the
/// last.</param>
/// <param name="ThreadId">The managed id of the thread that logged it.</param>
/// <param name="File">The caller's source file, as the compiler saw it: a full path on the machine that built it, or
/// its path in the repository after <c>/_/</c> in Release.</param>
/// <param name="Line">The caller's line in <paramref name="File"/>.</param>
/// <param name="Member">The method or property that logged it.</param>
public readonly record struct LogLine(
    DateTime Time,
    LogLevel Level,
    string Message,
    ulong? Frame,
    int ThreadId,
    string File,
    int Line,
    string Member)
{
    /// <summary>
    /// The line as the terminal and crash files show it, with the file's name only:
    /// <c>12:34:56.789 Warn     f1234 t1 Message (File.cs:60 Member)</c>.
    /// </summary>
    public override string ToString()
    {
        string frame = Frame is { } index ? index.ToString(CultureInfo.InvariantCulture) : "-";
        string file = Path.GetFileName(File);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Time:HH:mm:ss.fff} {Level,-8} f{frame} t{ThreadId} {Message} ({file}:{Line} {Member})");
    }
}
