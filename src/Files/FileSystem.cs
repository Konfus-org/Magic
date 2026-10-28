using System.Diagnostics.CodeAnalysis;
using System.IO;
using Magic.Utils;

namespace Magic.Files;

/// <summary>
/// The real file system: forwards to System.IO and wraps FileSystemWatcher.
/// </summary>
[SuppressMessage(
    "ApiDesign",
    "RS0030:Do not use banned APIs",
    Justification = "The IFileSystem implementation is the one place that touches the disk."
)]
public sealed class FileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Result CreateDirectory(string path) =>
        Run(() =>
        {
            _ = Directory.CreateDirectory(path);
        });

    public Result<string[]> EnumerateFiles(string path, string searchPattern, bool recursive) =>
        Run(() =>
            Directory.GetFiles(
                path,
                searchPattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly
            )
        );

    public Result<string[]> EnumerateDirectories(string path) => Run(() => Directory.GetDirectories(path));

    public Result<string[]> ReadLines(string path) => Run(() => File.ReadAllLines(path));

    public Result<string> ReadAllText(string path) => Run(() => File.ReadAllText(path));

    public Result<byte[]> ReadAllBytes(string path) => Run(() => File.ReadAllBytes(path));

    public Result Write(string path, string contents) => Run(() => File.WriteAllText(path, contents));

    public Result Copy(string sourceFile, string destinationFile) =>
        Run(() => File.Copy(sourceFile, destinationFile, overwrite: true));

    public Result DeleteDirectory(string path) => Run(() => Directory.Delete(path, recursive: true));

    public Result<IFileWatcher> Watch(string path, string filter) =>
        Run<IFileWatcher>(() => new FileWatcher(path, filter));

    /// <summary>
    /// <paramref name="work"/>'s value, or the reason the disk refused it. Anything else thrown is a bug and goes on.
    /// </summary>
    private static Result<T> Run<T>(Func<T> work)
    {
        try
        {
            return Result.Success(work());
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <inheritdoc cref="Run{T}(Func{T})"/>
    private static Result Run(Action work)
    {
        try
        {
            work();
            return Result.Success();
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// What the disk throws for a path that is missing, locked, malformed or not allowed.
    /// </summary>
    private static bool IsRefusal(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private sealed class FileWatcher : IFileWatcher
    {
        private readonly FileSystemWatcher _watcher;

        internal FileWatcher(string path, string filter)
        {
            _watcher = new FileSystemWatcher(path, filter)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            _watcher.Created += (_, e) => Created?.Invoke(this, e);
            _watcher.Changed += (_, e) => Changed?.Invoke(this, e);
            _watcher.Deleted += (_, e) => Deleted?.Invoke(this, e);
            _watcher.Renamed += (_, e) => Renamed?.Invoke(this, e);
            _watcher.EnableRaisingEvents = true;
        }

        public event EventHandler<FileSystemEventArgs>? Created;
        public event EventHandler<FileSystemEventArgs>? Changed;
        public event EventHandler<FileSystemEventArgs>? Deleted;
        public event EventHandler<RenamedEventArgs>? Renamed;

        public void Dispose() => _watcher.Dispose();
    }
}
