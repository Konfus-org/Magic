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
            using FileStream stream = OpenRead(path);

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

    /// <summary>Reads a whole text file (UTF-8, a BOM is dropped) on the calling thread. Fails like <see cref="ReadBinary"/>.</summary>
    public Result<string> ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using StreamReader reader = new(OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize);

            return Result<string>.Success(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Failure(ex.Message);
        }
    }

    /// <summary>Lists the entries of <paramref name="path"/> on the calling thread.</summary>
    public Result<string[]> ReadDirectory(string path, string? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return EnumerateDirectory(path, filter, SearchOption.TopDirectoryOnly);
    }

    /// <summary>Lists the entries under <paramref name="path"/>, subfolders included, on the calling thread.</summary>
    public Result<string[]> ReadDirectoryRecursive(string path, string? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return EnumerateDirectory(path, filter, SearchOption.AllDirectories);
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

    public IDisposable Watch(string path, string? filter, Action<string> changed, bool recursive = false)
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
        watcher.Deleted += (_, e) => changed(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            changed(e.OldFullPath);
            changed(e.FullPath);
        };
        watcher.Error += (_, e) => Debugging.Log.Warn($"Watching {path} hit an error; changes may have been missed. {e.GetException()}");
        watcher.EnableRaisingEvents = true;

        return watcher;
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
    private static FileStream OpenRead(string path)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = BufferSize,
                Options = FileOptions.SequentialScan
            });
    }

    private static void CreateParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return;

        Directory.CreateDirectory(directory);
    }
}
