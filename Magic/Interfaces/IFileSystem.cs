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

    /// <summary>
    /// The folder holding <paramref name="path"/>, or null at a root.
    /// </summary>
    string? Parent(string path);

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="root"/>.
    /// </summary>
    string Relative(string root, string path);

    /// <summary>
    /// The folder and file names <paramref name="path"/> is made of, in order, without separators.
    /// </summary>
    string[] Segments(string path);

    /// <summary>
    /// Is <paramref name="path"/> <paramref name="root"/> itself or somewhere beneath it?
    /// </summary>
    bool IsUnder(string root, string path);

    /// <summary>
    /// A file or a folder.
    /// </summary>
    bool Exists(string path);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>
    /// Reads a whole file on the calling thread. A failed result means it could not be read: missing, locked, or still being written.
    /// </summary>
    Result<byte[]> ReadBinary(string path);

    /// <summary>
    /// Reads a whole text file (UTF-8, a BOM is dropped) on the calling thread. Fails like <see cref="ReadBinary"/>.
    /// </summary>
    Result<string> ReadText(string path);

    /// <summary>
    /// <see cref="ReadBinary"/> without holding a thread while the disk works. Cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<Result<byte[]>> ReadBinaryAsync(string path, CancellationToken cancel = default);

    /// <summary>
    /// <see cref="ReadText"/> without holding a thread while the disk works. Cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<Result<string>> ReadTextAsync(string path, CancellationToken cancel = default);

    /// <summary>
    /// Reads a whole file into a buffer of this file system's own and hands its bytes (a UTF-8 byte order mark
    /// dropped) to <paramref name="parse"/>, whose answer is the result: for a file that is read once and kept as
    /// something else, as every JSON sidecar is, without making a text or an array of it on the way. The bytes
    /// are only good inside the call. Fails like <see cref="ReadBinary"/>; what <paramref name="parse"/> throws
    /// comes through as it is.
    /// </summary>
    Result<T> Read<T>(string path, FileParser<T> parse);

    /// <summary>
    /// <see cref="Read{T}"/> without holding a thread while the disk works. Cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<Result<T>> ReadAsync<T>(string path, FileParser<T> parse, CancellationToken cancel = default);

    /// <summary>
    /// One XxHash128 of the files' contents, in the order given, as 32 hex characters; a failed result names the first
    /// that could not be read. The files are streamed, never held whole: for a stamp of something big.
    /// </summary>
    Task<Result<string>> HashAsync(IReadOnlyList<string> paths, CancellationToken cancel = default);

    /// <summary>
    /// Lists the entries of <paramref name="path"/> on the calling thread.
    /// </summary>
    Result<string[]> ReadDirectory(string path, string? filter = null);

    /// <summary>
    /// Lists the entries under <paramref name="path"/>, subfolders included, on the calling thread.
    /// </summary>
    Result<string[]> ReadDirectoryRecursive(string path, string? filter = null);

    /// <summary>
    /// Writes a whole text file (UTF-8, no BOM) on the calling thread, creating its folder if needed.
    /// </summary>
    Result WriteText(string path, string text);

    /// <summary>
    /// Writes a whole binary file on the calling thread, creating its folder if needed.
    /// </summary>
    Result WriteBinary(string path, byte[] data);

    /// <summary>
    /// <see cref="WriteText"/> without holding a thread while the disk works. Cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<Result> WriteTextAsync(string path, string text, CancellationToken cancel = default);

    /// <summary>
    /// <see cref="WriteBinary"/> without holding a thread while the disk works. Cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<Result> WriteBinaryAsync(string path, byte[] data, CancellationToken cancel = default);

    /// <summary>
    /// Removes the folder at <paramref name="path"/> with everything under it, on the calling thread. One that is
    /// not there is a success: it is gone either way.
    /// </summary>
    Result DeleteDirectory(string path);

    /// <summary>
    /// Reports every path under <paramref name="path"/> that was created, changed, renamed (both names) or removed to
    /// <paramref name="changed"/> until the handle is disposed: whether it still exists says which. With
    /// <paramref name="recursive"/>, subfolders too; a folder renamed or removed is reported by its own path only.
    /// Calls arrive on a worker thread.
    /// </summary>
    IDisposable Watch(string path, string? filter, Action<string> changed, bool recursive = false);
}

/// <summary>
/// What <see cref="IFileSystem.Read{T}"/> makes of a file's bytes.
/// </summary>
public delegate T FileParser<out T>(ReadOnlySpan<byte> bytes);
