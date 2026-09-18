using Magic.Interfaces;
using Magic.Utils;
using System.Text;

namespace Magic.Services;

public sealed class FileOperations : IFileOperations
{
    private const int BufferSize = 64 * 1024;


    public bool Exists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.Exists(path) || Directory.Exists(path);
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
        long total = CountEntries(path, pattern, searchOption, cancellationToken);
        List<string> entries = [];
        long current = 0;

        progress?.Report(total == 0 ? 1d : 0d);

        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     path,
                     pattern,
                     searchOption))
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
        return Result<string[]>.Success(entries.ToArray());
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

    private static FileStream OpenRead(string path)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
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
