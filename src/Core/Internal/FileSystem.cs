using System.Diagnostics.CodeAnalysis;
using System.IO;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// The disk through System.IO: the one place that touches files and knows what a separator is. Beside what gems ask
/// for, it finds and watches the files under a folder, which only <see cref="Assets"/> does.
/// </summary>
internal sealed class FileSystem : IFileSystem
{
    private static readonly EnumerationOptions _under = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    public string Combine(params ReadOnlySpan<string> segments) => Path.Join(segments);

    public string Parent(string path) => Path.GetDirectoryName(path) ?? path;

    public bool FileExists(string path) => File.Exists(path);

    public bool FolderExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> Find(string folder, string extension)
    {
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            if (extension.Length == 0 || string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(file);
            }
        }

        return found;
    }

    /// <summary>
    /// The files with <paramref name="extension"/> at any depth under <paramref name="folder"/>, compared ignoring case,
    /// each as its path from <paramref name="folder"/> with <c>/</c> between folders on every OS: the form an asset path
    /// takes. Folders it may not read are skipped.
    /// </summary>
    public static IReadOnlyList<string> FindUnder(string folder, string extension)
    {
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(folder, "*", _under))
        {
            if (extension.Length == 0 || string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(Relative(folder, file));
            }
        }

        return found;
    }

    /// <summary>
    /// Calls <paramref name="changed"/> with the path, from <paramref name="folder"/> with <c>/</c> between folders, of
    /// each file under it that is created, written, renamed (both its old and its new path) or deleted, and each folder
    /// that is created, renamed or deleted (a file written in one is reported itself), until the subscription is
    /// disposed. On a thread of the watcher's, so it hands the path on rather than doing the work. Every OS reports
    /// changes its own way, doubled, late or out of order, so a call means only that something changed there: check what
    /// exists. <paramref name="folder"/> must exist.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The subscription owns the watcher, and disposing it disposes the watcher.")]
    public static Subscription Watch(string folder, Action<string> changed)
    {
        FileSystemWatcher watcher = new(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,

            // Room for a burst, such as a build copying a folder of files at once, before the OS drops what follows.
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => changed(Relative(folder, e.FullPath));

        // A folder is "written" whenever a file in it is, and that file is reported itself.
        watcher.Changed += (_, e) =>
        {
            if (!Directory.Exists(e.FullPath))
            {
                changed(Relative(folder, e.FullPath));
            }
        };
        watcher.Deleted += (_, e) => changed(Relative(folder, e.FullPath));
        watcher.Renamed += (_, e) =>
        {
            changed(Relative(folder, e.OldFullPath));
            changed(Relative(folder, e.FullPath));
        };
        watcher.Error += (_, e) => Debugging.Warn($"Watching {folder} missed changes: {e.GetException().Message}");
        watcher.EnableRaisingEvents = true;
        return new Subscription(watcher.Dispose);
    }

    public Result<byte[]> Read(string path)
    {
        try
        {
            return Result.Success(File.ReadAllBytes(path));
        }
        catch (IOException ex)
        {
            return Result.Failure<byte[]>(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Failure<byte[]>(ex.Message);
        }
    }

    public async Task<Result<byte[]>> ReadAsync(string path, CancellationToken cancel)
    {
        try
        {
            return Result.Success(await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false));
        }
        catch (IOException ex)
        {
            return Result.Failure<byte[]>(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Failure<byte[]>(ex.Message);
        }
    }

    public async Task<Result> WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancel)
    {
        // Moving a file over another replaces it at once, on every OS.
        string beside = $"{path}.{Path.GetRandomFileName()}";
        try
        {
            Directory.CreateDirectory(Parent(path));
            await File.WriteAllBytesAsync(beside, bytes, cancel).ConfigureAwait(false);
            File.Move(beside, path, overwrite: true);
            return Result.Success();
        }
        catch (IOException ex)
        {
            return Result.Failure(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Failure(ex.Message);
        }
        finally
        {
            if (File.Exists(beside))
            {
                File.Delete(beside);
            }
        }
    }

    public Result Delete(string path)
    {
        if (!File.Exists(path))
        {
            return Result.Success();
        }

        try
        {
            File.Delete(path);
            return Result.Success();
        }
        catch (IOException ex)
        {
            return Result.Failure(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    /// <summary>
    /// <paramref name="path"/> from <paramref name="folder"/>, with <c>/</c> between folders on every OS.
    /// </summary>
    private static string Relative(string folder, string path)
    {
        return Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
    }
}
