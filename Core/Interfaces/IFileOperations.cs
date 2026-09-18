using Magic.Utils;

namespace Magic.Interfaces;

internal interface IFileOperations
{
    bool Exists(string path);
    Task<Result<byte[]>> ReadBinaryAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result<string>> ReadTextAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result> WriteBytesAsync(string path, byte[] data, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result> WriteTextAsync(string path, string data, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result> CopyAsync(string sourcePath, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result<string[]>> ReadDirectoryAsync(string path, string? filter = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<Result<string[]>> ReadDirectoryRecursiveAsync(string path, string? filter = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

