using Magic.Interfaces;
using Magic.Utils;

namespace Magic.UnitTests.Fakes;

/// <summary>An <see cref="IFileSystem"/> with no disk under it: it keeps what is written, and nothing can be read.</summary>
internal sealed class FakeFileSystem : IFileSystem
{
    /// <summary>Every file written, by path.</summary>
    public Dictionary<string, byte[]> Written { get; } = [];

    public string FullPath(string path) => path;
    public string Combine(params string[] parts) => string.Join('/', parts);
    public string? Parent(string path) => Path.GetDirectoryName(path);
    public string Relative(string root, string path) => Path.GetRelativePath(root, path);
    public bool Exists(string path) => Written.ContainsKey(path);
    public bool FileExists(string path) => Written.ContainsKey(path);
    public bool DirectoryExists(string path) => false;
    public Result<byte[]> ReadBinary(string path) => Result<byte[]>.Failure("fake");
    public Result<string> ReadText(string path) => Result<string>.Failure("fake");
    public Result<string[]> ReadDirectory(string path, string? filter = null) => Result<string[]>.Failure("fake");
    public Result<string[]> ReadDirectoryRecursive(string path, string? filter = null) => Result<string[]>.Failure("fake");
    public Result WriteText(string path, string text) => WriteBinary(path, System.Text.Encoding.UTF8.GetBytes(text));

    public Result WriteBinary(string path, byte[] data)
    {
        Written[path] = data;
        return Result.Success();
    }

    public IDisposable Watch(string path, string? filter, Action<string> changed, bool recursive = false) => throw new NotSupportedException();
}
