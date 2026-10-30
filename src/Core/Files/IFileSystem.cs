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
    Result CreateDirectory(string path);
    Result<string[]> EnumerateFiles(string path, string searchPattern, bool recursive);
    Result<string[]> EnumerateDirectories(string path);
    Result<string[]> ReadLines(string path);

    /// <summary><see cref="ReadLines"/> without holding a thread while the disk works.</summary>
    Task<Result<string[]>> ReadLinesAsync(string path, CancellationToken cancel);

    Result<string> ReadText(string path);

    /// <summary><see cref="ReadText"/> without holding a thread while the disk works.</summary>
    Task<Result<string>> ReadTextAsync(string path, CancellationToken cancel);

    Result<byte[]> ReadBytes(string path);

    /// <summary><see cref="ReadBytes"/> without holding a thread while the disk works.</summary>
    Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken cancel);

    Result Write(string path, string contents);

    /// <summary>
    /// Writes <paramref name="bytes"/> as the whole file at <paramref name="path"/>, making its folders. It is written
    /// beside the file and moved over it, so a reader, or a crash, finds it whole: as it was or as it is now.
    /// </summary>
    Task<Result> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancel);

    /// <summary>
    /// Deletes the file or empty folder at <paramref name="path"/>; nothing happens when there is none.
    /// </summary>
    Result Delete(string path);

    /// <summary>
    /// Deletes the file or folder at <paramref name="path"/>; nothing happens when there is none.
    /// <paramref name="recursive"/> applies only to a folder: it also deletes everything inside it, without which a
    /// folder that isn't empty is refused.
    /// </summary>
    Result Delete(string path, bool recursive);

    Result Copy(string sourceFile, string destinationFile);

    /// <summary>Watch a directory tree for changes to files matching <paramref name="filter"/>.</summary>
    Result<IFileWatcher> Watch(string path, string filter = "*.*");
}
