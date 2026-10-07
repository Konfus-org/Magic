using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Buffers;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;

#pragma warning disable RS0030 // this is the IFileSystem that everything else goes through

namespace Magic.Services;

/// <summary>
/// The real disk. See <see cref="IFileSystem"/>.
/// </summary>
internal sealed class FileSystem : IFileSystem
{
    private const int BufferSize = 64 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // The one place that knows what separates a path: everything else asks through IFileSystem.
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

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

    public string[] Segments(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
    }

    public bool IsUnder(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Anything outside the root comes back climbing out of it ("..") or, on another drive, still rooted.
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && Segments(relative)[0] != "..";
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

    /// <summary>
    /// Reads a whole file on the calling thread. A failed result means it could not be read: missing, locked, or still being written.
    /// </summary>
    public Result<byte[]> ReadBinary(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using FileStream stream = OpenRead(path, FileOptions.None);

            if (stream.Length > int.MaxValue)
                return Result<byte[]>.Failure("Files larger than 2 GB cannot be loaded into a byte array.");

            byte[] data = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(data);

            return Result<byte[]>.Success(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<byte[]>.Failure(ex.Message);
        }
    }

    public Result<string> ReadText(string path) => Read(path, Text);

    public async Task<Result<byte[]>> ReadBinaryAsync(string path, CancellationToken cancel = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            FileStream stream = OpenRead(path, FileOptions.Asynchronous);
            await using ConfiguredAsyncDisposable disposing = stream.ConfigureAwait(false);

            if (stream.Length > int.MaxValue)
                return Result<byte[]>.Failure("Files larger than 2 GB cannot be loaded into a byte array.");

            byte[] data = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            await stream.ReadExactlyAsync(data, cancel).ConfigureAwait(false);

            return Result<byte[]>.Success(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<byte[]>.Failure(ex.Message);
        }
    }

    public Task<Result<string>> ReadTextAsync(string path, CancellationToken cancel = default) => ReadAsync(path, Text, cancel);

    public Result<T> Read<T>(string path, FileParser<T> parse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(parse);

        try
        {
            using FileStream stream = OpenRead(path, FileOptions.None);
            if (stream.Length > int.MaxValue)
                return Result<T>.Failure("Files larger than 2 GB cannot be read whole.");

            int length = (int)stream.Length;
            // A pooled buffer, not a StreamReader's own: the engine reads thousands of small files (every asset's sidecar).
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                stream.ReadExactly(buffer, 0, length);
                return Result<T>.Success(parse(((ReadOnlySpan<byte>)buffer.AsSpan(0, length)).WithoutBom()));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<T>.Failure(ex.Message);
        }
    }

    public async Task<Result<T>> ReadAsync<T>(string path, FileParser<T> parse, CancellationToken cancel = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(parse);

        try
        {
            FileStream stream = OpenRead(path, FileOptions.Asynchronous);
            await using ConfiguredAsyncDisposable disposing = stream.ConfigureAwait(false);
            if (stream.Length > int.MaxValue)
                return Result<T>.Failure("Files larger than 2 GB cannot be read whole.");

            int length = (int)stream.Length;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancel).ConfigureAwait(false);
                return Result<T>.Success(parse(((ReadOnlySpan<byte>)buffer.AsSpan(0, length)).WithoutBom()));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<T>.Failure(ex.Message);
        }
    }

    public async Task<Result<string>> HashAsync(IReadOnlyList<string> paths, CancellationToken cancel = default)
    {
        XxHash128 hash = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (string path in paths)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                FileStream stream = OpenRead(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using ConfiguredAsyncDisposable disposing = stream.ConfigureAwait(false);
                for (int read; (read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0;)
                    hash.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Result<string>.Success(Convert.ToHexString(hash.GetCurrentHash()));
    }

    /// <summary>
    /// Lists the entries of <paramref name="path"/> on the calling thread.
    /// </summary>
    public Result<string[]> ReadDirectory(string path, string? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return EnumerateDirectory(path, filter, SearchOption.TopDirectoryOnly);
    }

    /// <summary>
    /// Lists the entries under <paramref name="path"/>, subfolders included, on the calling thread.
    /// </summary>
    public Result<string[]> ReadDirectoryRecursive(string path, string? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return EnumerateDirectory(path, filter, SearchOption.AllDirectories);
    }

    /// <summary>
    /// Writes a whole text file (UTF-8, no BOM) on the calling thread, creating its folder if needed.
    /// </summary>
    public Result WriteText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            CreateParentDirectory(path);
            File.WriteAllText(path, text, Utf8);

            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Writes a whole binary file on the calling thread, creating its folder if needed.
    /// </summary>
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

    public async Task<Result> WriteTextAsync(string path, string text, CancellationToken cancel = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            CreateParentDirectory(path);
            await File.WriteAllTextAsync(path, text, Utf8, cancel).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result> WriteBinaryAsync(string path, byte[] data, CancellationToken cancel = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            CreateParentDirectory(path);
            await File.WriteAllBytesAsync(path, data, cancel).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(ex.Message);
        }
    }

    public Result DeleteDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            Directory.Delete(path, recursive: true);

            return Result.Success();
        }
        catch (DirectoryNotFoundException)
        {
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(ex.Message);
        }
    }

    public Subscription Watch(string path, string? filter, Action<string> changed, bool recursive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

#pragma warning disable CA2000 // the subscription owns it and disposes it
        FileSystemWatcher watcher = new(path, filter ?? "*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = recursive,
            InternalBufferSize = 64 * 1024, // the maximum; a big save or checkout raises many events at once
        };
#pragma warning restore CA2000

        watcher.Created += (_, args) => changed(args.FullPath);
        watcher.Changed += (_, args) => changed(args.FullPath);
        watcher.Deleted += (_, args) => changed(args.FullPath);
        watcher.Renamed += (_, args) =>
        {
            changed(args.OldFullPath);
            changed(args.FullPath);
        };
        watcher.Error += (_, args) => Log.Warn($"Watching {path} hit an error; changes may have been missed. {args.GetException()}");
        watcher.EnableRaisingEvents = true;

        return new Subscription(watcher.Dispose);
    }

    private static Result<string[]> EnumerateDirectory(string path, string? filter, SearchOption searchOption)
    {
        try
        {
            return Result<string[]>.Success(Directory.GetFileSystemEntries(path, filter ?? "*", searchOption));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // DirectoryNotFoundException is an IOException. Callers get a failed Result, as the contract promises.
            return Result<string[]>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Opens for reading without blocking anyone else: the engine reads files (gems, assets) that builds and
    /// editors rewrite while it runs, so a writer landing mid-read fails on this side, where the caller can
    /// retry, never on the writer's.
    /// </summary>
    private static FileStream OpenRead(string path, FileOptions options)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = BufferSize,
                Options = FileOptions.SequentialScan | options
            });
    }

    private static void CreateParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return;

        Directory.CreateDirectory(directory);
    }

    private static string Text(ReadOnlySpan<byte> bytes) => Utf8.GetString(bytes);
}
