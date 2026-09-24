using Magic.Interfaces;
using Magic.Utils;
using System.Text;

namespace Magic.Services;

/// <summary>The real disk. See <see cref="IFileSystem"/>.</summary>
public sealed class FileSystem : IFileSystem
{
    private const int BufferSize = 64 * 1024;

    public string FullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetFullPath(path);
    }

    public string Combine(params string[] parts)
    {
        return Path.Combine(parts);
    }

    public string? Parent(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetDirectoryName(path);
    }

    public string Relative(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetRelativePath(root, path);
    }

    public bool FileExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.Exists(path);
    }

    public bool DirectoryExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Directory.Exists(path);
    }

    public bool Exists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>Reads a whole file on the calling thread. A failed result means it could not be read: missing, locked, or still being written.</summary>
    public Result<byte[]> ReadBinary(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using FileStream stream = OpenRead(path, FileOptions.SequentialScan);

            if (stream.Length > int.MaxValue)
            {
                return Result<byte[]>.Failure("Files larger than 2 GB cannot be loaded into a byte array.");
            }

            byte[] data = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(data);
            return Result<byte[]>.Success(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<byte[]>.Failure(ex.Message);
        }
    }

    /// <summary>Lists the entries of <paramref name="path"/> on the calling thread.</summary>
    public Result<string[]> ReadDirectory(string path, string? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return EnumerateDirectory(path, filter, SearchOption.TopDirectoryOnly, null, default);
    }

    /// <summary>Lists the entries under <paramref name="path"/>, subfolders included, on the calling thread.</summary>
    public Result<string[]> ReadDirectoryRecursive(string path, string? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return EnumerateDirectory(path, filter, SearchOption.AllDirectories, null, default);
    }

    /// <summary>Writes a whole text file (UTF-8, no BOM) on the calling thread, creating its folder if needed.</summary>
    public Result WriteText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            CreateParentDirectory(path);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>Writes a whole binary file on the calling thread, creating its folder if needed.</summary>
    public Result WriteBinary(string path, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            CreateParentDirectory(path);
            File.WriteAllBytes(path, data);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Reports created or changed files under <paramref name="path"/> to <paramref name="changed"/> and removed ones to
    /// <paramref name="deleted"/> (a rename is both) until the handle is disposed; with <paramref name="recursive"/>,
    /// subfolders too, and a folder renamed or removed is reported by its own path only. Callbacks arrive on a worker thread.
    /// </summary>
    public IDisposable Watch(string path, string? filter, Action<string> changed, Action<string> deleted, bool recursive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileSystemWatcher watcher = new(path, filter ?? "*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = recursive,
            InternalBufferSize = 64 * 1024, // the maximum; a big save or checkout raises many events at once
        };
        watcher.Created += (_, e) => changed(e.FullPath);
        watcher.Changed += (_, e) => changed(e.FullPath);
        watcher.Deleted += (_, e) => deleted(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            deleted(e.OldFullPath);
            changed(e.FullPath);
        };
        watcher.Error += (_, e) => Debugging.LogWarning($"Watching {path} hit an error; changes may have been missed. {e.GetException()}");
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    public async Task<Result<byte[]>> ReadBinaryAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using FileStream stream = OpenRead(path);

        if (stream.Length > int.MaxValue)
        {
            return Result<byte[]>.Failure(
                "Files larger than 2 GB cannot be loaded into a byte array.");
        }

        byte[] data = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        int offset = 0;

        while (offset < data.Length)
        {
            int read = await stream.ReadAsync(
                data.AsMemory(offset),
                cancellationToken);

            if (read == 0)
                break;

            offset += read;

            if (data.Length > 0)
            {
                progress?.Report((double)offset / data.Length);
            }
        }

        progress?.Report(1d);
        return Result<byte[]>.Success(data);
    }

    public async Task<Result<string>> ReadTextAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using FileStream stream = OpenRead(path);

        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: BufferSize);

        StringBuilder text = new();
        char[] buffer = GC.AllocateUninitializedArray<char>(BufferSize);
        long length = stream.Length;

        while (true)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);

            if (read == 0)
                break;

            text.Append(buffer, 0, read);

            if (length > 0)
            {
                progress?.Report(Math.Min(1d, (double)stream.Position / length));
            }
        }

        progress?.Report(1d);
        return Result<string>.Success(text.ToString());
    }

    public async Task<Result> WriteBytesAsync(
        string path,
        byte[] data,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);

        CreateParentDirectory(path);

        await using FileStream stream = OpenWrite(path);

        int offset = 0;

        while (offset < data.Length)
        {
            int count = Math.Min(BufferSize, data.Length - offset);

            await stream.WriteAsync(
                data.AsMemory(offset, count),
                cancellationToken);

            offset += count;
            progress?.Report(data.Length == 0 ? 1d : (double)offset / data.Length);
        }

        await stream.FlushAsync(cancellationToken);
        progress?.Report(1d);

        return Result.Success();
    }

    public async Task<Result> WriteTextAsync(
        string path,
        string data,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);

        CreateParentDirectory(path);

        await using FileStream stream = OpenWrite(path);

        using StreamWriter writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            BufferSize,
            leaveOpen: true);

        await writer.WriteAsync(data.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);

        progress?.Report(1d);

        return Result.Success();
    }

    public async Task<Result> CopyAsync(
        string sourcePath,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string fullSourcePath = Path.GetFullPath(sourcePath);
        string fullDestinationPath = Path.GetFullPath(destinationPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fullSourcePath, fullDestinationPath, comparison))
        {
            return Result.Failure("The source and destination paths must be different.");
        }

        await using FileStream source = OpenRead(fullSourcePath);

        CreateParentDirectory(fullDestinationPath);

        await using FileStream destination = OpenWrite(fullDestinationPath);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        long length = source.Length;
        long copied = 0;

        while (true)
        {
            int read = await source.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);

            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);

            copied += read;

            if (length > 0)
            {
                progress?.Report(Math.Min(1d, (double)copied / length));
            }
        }

        await destination.FlushAsync(cancellationToken);
        progress?.Report(1d);

        return Result.Success();
    }

    public Task<Result<string[]>> ReadDirectoryAsync(
        string path,
        string? filter = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(
            () => EnumerateDirectory(
                path,
                filter,
                SearchOption.TopDirectoryOnly,
                progress,
                cancellationToken),
            cancellationToken);
    }

    public Task<Result<string[]>> ReadDirectoryRecursiveAsync(
        string path,
        string? filter = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(
            () => EnumerateDirectory(
                path,
                filter,
                SearchOption.AllDirectories,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private static Result<string[]> EnumerateDirectory(
        string path,
        string? filter,
        SearchOption searchOption,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string pattern = filter ?? "*";
        try
        {
            long total = CountEntries(path, pattern, searchOption, cancellationToken);
            List<string> entries = [];
            long current = 0;

            progress?.Report(total == 0 ? 1d : 0d);

            foreach (string entry in Directory.EnumerateFileSystemEntries(path, pattern, searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
                current++;

                if (total > 0)
                {
                    progress?.Report(Math.Min(1d, (double)current / total));
                }
            }

            progress?.Report(1d);
            return Result<string[]>.Success([.. entries]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // DirectoryNotFoundException is an IOException. Callers get a failed Result, as the contract promises.
            return Result<string[]>.Failure(ex.Message);
        }
    }

    private static long CountEntries(
        string path,
        string filter,
        SearchOption searchOption,
        CancellationToken cancellationToken)
    {
        long count = 0;

        foreach (string _ in Directory.EnumerateFileSystemEntries(
                     path,
                     filter,
                     searchOption))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    /// <summary>
    /// Opens for reading without blocking anyone else: the engine reads files (gems, assets) that builds and
    /// editors rewrite while it runs, so a writer landing mid-read fails on this side, where the caller can
    /// retry, never on the writer's.
    /// </summary>
    private static FileStream OpenRead(string path, FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = BufferSize,
                Options = options
            });
    }

    private static FileStream OpenWrite(string path)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
    }

    private static void CreateParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
