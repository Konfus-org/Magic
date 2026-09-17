using Core.Interfaces;
using Core.Utils;
using System.Text;

namespace Core.Services;

public sealed class FileOperations : IFileOperations
{
    private const int BufferSize = 64 * 1024;

    public async Task<Result<byte[]>> ReadBinaryAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] data = new byte[stream.Length];
        int offset = 0;

        while (offset < data.Length)
        {
            int read = await stream.ReadAsync(
                data.AsMemory(offset),
                cancellationToken);

            if (read == 0)
                break;

            offset += read;
            progress?.Report((double)offset / data.Length);
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

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: BufferSize);

        StringBuilder text = new();
        char[] buffer = new char[BufferSize];
        long length = stream.Length;
        int offset = 0;

        while (offset < length)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);

            if (read == 0)
                break;

            text.Append(buffer, 0, read);

            offset += read;
            progress?.Report((double)offset / length);
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

        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

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

    public Task<Result> WriteTextAsync(
        string path,
        string data,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        return WriteBytesAsync(
            path,
            Encoding.UTF8.GetBytes(data),
            progress,
            cancellationToken);
    }

    public Task<Result<string[]>> ReadDirectoryAsync(
        string path,
        string? filter = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        string[] entries = Directory.GetFileSystemEntries(path, filter ?? "*");
        progress?.Report(1d);

        return Task.FromResult(Result<string[]>.Success(entries));
    }

    public Task<Result<string[]>> ReadDirectoryRecursiveAsync(
        string path,
        string? filter = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        string[] entries = Directory.GetFileSystemEntries(
            path,
            filter ?? "*",
            SearchOption.AllDirectories);

        progress?.Report(1d);

        return Task.FromResult(Result<string[]>.Success(entries));
    }
}
