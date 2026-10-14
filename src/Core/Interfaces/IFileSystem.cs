using Magic.Contexts.Utils;

namespace Magic.Interfaces;

/// <summary>
/// The disk. Every file access goes through it (the build refuses File, Directory and the separators elsewhere), so
/// paths are built, taken apart and read the same way on every OS.
/// </summary>
public interface IFileSystem
{
    /// <summary>
    /// Joins path segments with this OS's separator.
    /// </summary>
    string Combine(params ReadOnlySpan<string> segments);

    /// <summary>
    /// The folder <paramref name="path"/> is in.
    /// </summary>
    string Parent(string path);

    bool FileExists(string path);

    bool FolderExists(string path);

    /// <summary>
    /// The files directly in <paramref name="folder"/> with <paramref name="extension"/> (such as ".magic"),
    /// compared ignoring case.
    /// </summary>
    IReadOnlyList<string> Find(string folder, string extension);

    /// <summary>
    /// The whole file, or why it could not be read.
    /// </summary>
    Result<byte[]> Read(string path);

    /// <summary>
    /// <see cref="Read"/> without holding a thread while the disk works: for loading in the background.
    /// </summary>
    Task<Result<byte[]>> ReadAsync(string path, CancellationToken cancel);

    /// <summary>
    /// Writes <paramref name="bytes"/> as the whole file at <paramref name="path"/>, making the folders it is in. They are
    /// written beside it, then moved over it, so whoever reads the file meanwhile, or after a crash, finds it whole: as it
    /// was or as it is now.
    /// </summary>
    Task<Result> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancel);

    /// <summary>
    /// Deletes the file at <paramref name="path"/>; nothing happens when there is none.
    /// </summary>
    Result Delete(string path);
}
