using Magic.Utils;

namespace Magic.Files;

/// <summary>
/// Abstraction over the file system so services that touch disk can be tested with a mock. Work the disk can refuse
/// returns a <see cref="Result"/> with the reason.
/// </summary>
public interface IFileSystem
{
    /// <summary>The path <paramref name="parts"/> name, joined with this OS's separator.</summary>
    string Combine(params ReadOnlySpan<string> parts);

    /// <summary>The folder that holds <paramref name="path"/>; empty for a root.</summary>
    string GetParent(string path);

    bool FileExists(string path);
    bool DirectoryExists(string path);
    Result<string[]> EnumerateFiles(string path, string searchPattern, bool recursive);
    Result<byte[]> ReadBytes(string path);

    /// <summary><see cref="ReadBytes"/> without holding a thread while the disk works.</summary>
    Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken cancel);

    /// <summary>
    /// Writes <paramref name="bytes"/> as the whole file at <paramref name="path"/>, making its folders. It is written
    /// beside the file and moved over it, so a reader, or a crash, finds it whole: as it was or as it is now.
    /// </summary>
    Task<Result> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancel);

    /// <summary>
    /// Watches the directory tree under <paramref name="path"/>, calling <paramref name="touched"/> with the full path
    /// of each file matching <paramref name="filter"/> that is made, written, deleted or renamed (its old path and its
    /// new one), on a background thread, until the watch is disposed.
    /// </summary>
    Result<IDisposable> Watch(string path, string filter, Action<string> touched);
}
