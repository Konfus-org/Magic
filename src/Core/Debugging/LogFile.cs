using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Magic.Files;
using Magic.Utils;

namespace Magic.Debugging;

/// <summary>
/// Writes the log of this process to a file of its own in <c>Logs/</c> beside Magic, where a crash file also goes.
/// </summary>
/// <remarks>
/// The file holds each line that the log's verbosity lets through, so it holds no verbose line unless the verbosity is
/// detailed. Each line is on disk as soon as it is logged, so a run that is killed keeps its log. The folder keeps
/// the files of the last runs, and the oldest go as a new one is made.
/// </remarks>
internal sealed class LogFile : Disposable, ILogSink
{
    /// <summary>
    /// How many log files the folder keeps, this run's among them.
    /// </summary>
    private const int KeptFiles = 20;

    private readonly StreamWriter _writer;

    private LogFile(StreamWriter writer) => _writer = Track(writer);

    /// <summary>
    /// Starts the log file of this process and gives its path to <see cref="Log.FilePath"/>, until the subscription
    /// ends. Without a file that can be written, it logs a warning and the run goes on with no log file.
    /// </summary>
    [SuppressMessage(
        "ApiDesign",
        "RS0030:Do not use banned APIs",
        Justification = "The file system has no file that stays open, and a log line must be on disk as it is logged."
    )]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The subscription that it returns disposes the file."
    )]
    public static Subscription Start(IFileSystem files)
    {
        string folder = files.Combine(AppContext.BaseDirectory, "Logs");

        // The time sorts the files by age, and the process id keeps two runs that start together apart.
        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss.fff}_{Environment.ProcessId}.log"
        );
        string path = files.Combine(folder, name);
        Result made = files.CreateDirectory(folder);
        if (!made.Ok)
            return StartWithout(path, made.Error);

        RemoveOldest(files, folder);
        LogFile file;
        try
        {
            // Another process can read the file while this one writes it.
            FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file = new LogFile(new StreamWriter(stream) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StartWithout(path, ex.Message);
        }

        Log.FilePath = path;
        Subscription registered = Log.Register(file);

        return new Subscription(() =>
        {
            registered.Dispose();
            Log.FilePath = null;
            file.Dispose();
        });
    }

    public void Write(in Log.Line line) => _writer.WriteLine(line.ToString());

    public void Flush() => _writer.Flush();

    /// <summary>
    /// Says that the run has no log file at <paramref name="path"/> and why, and gives a subscription that ends
    /// nothing.
    /// </summary>
    private static Subscription StartWithout(string path, string why)
    {
        Log.Warn($"The log file {path} could not be made, so this run has none: {why}");

        return new Subscription(() => { });
    }

    /// <summary>
    /// Deletes the oldest log files of <paramref name="folder"/>, so that it keeps <see cref="KeptFiles"/> with the
    /// one that is made next.
    /// </summary>
    /// <remarks>
    /// A file that another run still writes cannot be deleted, and stays.
    /// </remarks>
    private static void RemoveOldest(IFileSystem files, string folder)
    {
        Result<string[]> logs = files.EnumerateFiles(folder, "*.log", false);
        if (!logs.Ok)
            return;

        foreach (string old in logs.Value.OrderDescending().Skip(KeptFiles - 1))
        {
            Result deleted = files.Delete(old);
            if (!deleted.Ok)
                Log.Verbose($"The old log file {old} was not deleted: {deleted.Error}");
        }
    }
}
