using System.Diagnostics.CodeAnalysis;
using System.IO;
using Magic.Utils;
using Microsoft.Win32.SafeHandles;

namespace Magic.Files;

/// <summary>
/// The real file system: forwards to System.IO and wraps FileSystemWatcher.
/// </summary>
[SuppressMessage(
    "ApiDesign",
    "RS0030:Do not use banned APIs",
    Justification = "The IFileSystem implementation is the one place that touches the disk."
)]
internal sealed class FileSystem : IFileSystem
{
    public string Combine(params ReadOnlySpan<string> parts) => Path.Combine(parts);

    public string GetParent(string path) => Path.GetDirectoryName(path) ?? string.Empty;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    // System.IO gives 1601 for a missing file, not an error.
    public Result<DateTime> GetLastWriteTime(string path) =>
        Run(() =>
            File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : throw new FileNotFoundException($"{path} does not exist.", path)
        );

    public Result<(long Length, long Ticks)> GetStamp(string path) =>
        Run(() =>
        {
            FileInfo info = new(path);

            return info.Exists
                ? (info.Length, info.LastWriteTimeUtc.Ticks)
                : throw new FileNotFoundException($"{path} does not exist.", path);
        });

    public Result Touch(string path) => Run(() => File.SetLastWriteTimeUtc(path, DateTime.UtcNow));

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

    public Task<Result<string[]>> ReadLinesAsync(string path, CancellationToken cancel) =>
        RunAsync(() => File.ReadAllLinesAsync(path, cancel));

    public Result<string> ReadText(string path) => Run(() => File.ReadAllText(path));

    public Task<Result<string>> ReadTextAsync(string path, CancellationToken cancel) =>
        RunAsync(() => File.ReadAllTextAsync(path, cancel));

    public Result<byte[]> ReadBytes(string path) =>
        Run(() =>
        {
            using SafeFileHandle handle = OpenForRead(path, FileOptions.None);
            byte[] bytes = new byte[ReadableLength(handle)];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
                if (count == 0)
                    break;

                read += count;
            }

            return read == bytes.Length ? bytes : bytes[..read];
        });

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken cancel) =>
        RunAsync(async () =>
        {
            using SafeFileHandle handle = OpenForRead(path, FileOptions.Asynchronous);
            byte[] bytes = new byte[ReadableLength(handle)];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = await RandomAccess
                    .ReadAsync(handle, bytes.AsMemory(read), read, cancel)
                    .ConfigureAwait(false);
                if (count == 0)
                    break;

                read += count;
            }

            return read == bytes.Length ? bytes : bytes[..read];
        });

    public Result Write(string path, string contents) => Run(() => File.WriteAllText(path, contents));

    public async Task<Result> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancel)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            await File.WriteAllBytesAsync(temporary, bytes, cancel).ConfigureAwait(false);
            Replace(temporary, path);

            return Result.Success();
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return Result.Failure(ex.Message);
        }
        finally
        {
            // Left behind only when the write failed or was cancelled.
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public Result Delete(string path) => Delete(path, recursive: false);

    public Result Delete(string path, bool recursive) =>
        Run(() =>
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive);
            else
                File.Delete(path);
        });

    public Result Copy(string sourceFile, string destinationFile) =>
        Run(() => File.Copy(sourceFile, destinationFile, overwrite: true));

    public Result<IDisposable> Watch(string path, string filter, Action<string> touched) =>
        Run<IDisposable>(() =>
        {
            FileSystemWatcher watcher = new(path, filter)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, e) => touched(e.FullPath);
            watcher.Changed += (_, e) => touched(e.FullPath);
            watcher.Deleted += (_, e) => touched(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                touched(e.OldFullPath);
                touched(e.FullPath);
            };
            watcher.EnableRaisingEvents = true;

            return watcher;
        });

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

    /// <inheritdoc cref="Run{T}(Func{T})"/>
    private static async Task<Result<T>> RunAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Result.Success(await work().ConfigureAwait(false));
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

    /// <summary>
    /// Opens <paramref name="path"/> to read, so that a replace of the file does not collide with the read: Windows
    /// refuses a replace of a file that a handle holds open, unless the handle shares delete.
    /// </summary>
    private static SafeFileHandle OpenForRead(string path, FileOptions options) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, options);

    private static int ReadableLength(SafeFileHandle handle)
    {
        long length = RandomAccess.GetLength(handle);
        if (length > Array.MaxLength)
        {
            throw new IOException(
                $"The file is {length} bytes, more than the {Array.MaxLength} bytes that one array holds."
            );
        }

        return (int)length;
    }

    /// <summary>
    /// Puts <paramref name="from"/> in place of <paramref name="to"/>, in one step.
    /// </summary>
    /// <remarks>
    /// A move over a file refuses on Windows when any handle holds the file, also one that shares delete. A replace
    /// works when each reader shares delete (<see cref="OpenForRead"/>). It needs the file to exist, so a move makes
    /// the first one.
    /// </remarks>
    private static void Replace(string from, string to)
    {
        try
        {
            File.Replace(from, to, destinationBackupFileName: null);
        }
        catch (FileNotFoundException) when (File.Exists(from))
        {
            File.Move(from, to, overwrite: true);
        }
    }
}
