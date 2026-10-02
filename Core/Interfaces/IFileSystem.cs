using Magic.Utils;

namespace Magic.Interfaces;

/// <summary>
/// Every way the engine touches disk, so nothing else needs <c>System.IO</c>: the host, gems and tests go
/// through one of these, and a test can hand out a fake. Reads never block a writer (a build or an editor
/// rewriting the file mid-read fails on this side, where the caller can retry), and a failed
/// <see cref="Result"/> is the normal way to hear about a missing or locked file. Path methods are pure string
/// work on this file system's rules; they touch nothing.
/// </summary>
public interface IFileSystem
{
    string FullPath(string path);

    string Combine(params string[] parts);

    /// <summary>The folder holding <paramref name="path"/>, or null at a root.</summary>
    string? Parent(string path);

    /// <summary><paramref name="path"/> relative to <paramref name="root"/>.</summary>
    string Relative(string root, string path);

    /// <summary>The folder and file names <paramref name="path"/> is made of, in order, without separators.</summary>
    string[] Segments(string path);

    /// <summary>Is <paramref name="path"/> <paramref name="root"/> itself or somewhere beneath it?</summary>
    bool IsUnder(string root, string path);

    /// <summary>A file or a folder.</summary>
    bool Exists(string path);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Reads a whole file on the calling thread. A failed result means it could not be read: missing, locked, or still being written.</summary>
    Result<byte[]> ReadBinary(string path);

    /// <summary>Reads a whole text file (UTF-8, a BOM is dropped) on the calling thread. Fails like <see cref="ReadBinary"/>.</summary>
    Result<string> ReadText(string path);

    /// <summary>Lists the entries of <paramref name="path"/> on the calling thread.</summary>
    Result<string[]> ReadDirectory(string path, string? filter = null);

    /// <summary>Lists the entries under <paramref name="path"/>, subfolders included, on the calling thread.</summary>
    Result<string[]> ReadDirectoryRecursive(string path, string? filter = null);

    /// <summary>Writes a whole text file (UTF-8, no BOM) on the calling thread, creating its folder if needed.</summary>
    Result WriteText(string path, string text);

    /// <summary>Writes a whole binary file on the calling thread, creating its folder if needed.</summary>
    Result WriteBinary(string path, byte[] data);

    /// <summary>
    /// Reports every path under <paramref name="path"/> that was created, changed, renamed (both names) or removed to
    /// <paramref name="changed"/> until the handle is disposed: whether it still exists says which. With
    /// <paramref name="recursive"/>, subfolders too; a folder renamed or removed is reported by its own path only.
    /// Calls arrive on a worker thread.
    /// </summary>
    IDisposable Watch(string path, string? filter, Action<string> changed, bool recursive = false);
}
