using Core.Utils;

namespace Core.Interfaces;

internal interface IFileOperations
{
    Task<Result<byte[]>> ReadBinaryAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result<string>> ReadTextAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result> WriteBytesAsync(string path, byte[] data, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result> WriteTextAsync(string path, string data, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result<string[]>> ReadDirectoryAsync(string path, string? filter = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result<string[]>> ReadDirectoryRecursiveAsync(string path, string? filter = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

