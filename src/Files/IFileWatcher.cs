using System.IO;

namespace Magic.Files;

/// <summary>
/// Change notifications for files under a watched directory, carrying full paths.
/// Events fire on background threads.
/// </summary>
public interface IFileWatcher : IDisposable
{
    event EventHandler<FileSystemEventArgs>? Created;
    event EventHandler<FileSystemEventArgs>? Changed;
    event EventHandler<FileSystemEventArgs>? Deleted;
    event EventHandler<RenamedEventArgs>? Renamed;
}
