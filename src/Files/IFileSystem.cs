using Magic.State;

namespace Magic.Files;

/// <summary>
/// Abstraction over the file system so services that touch disk can be tested with a mock. Work the disk can refuse
/// returns a <see cref="Result"/> with the reason.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    Result CreateDirectory(string path);
    Result<string[]> EnumerateFiles(string path, string searchPattern, bool recursive);
    Result<string[]> EnumerateDirectories(string path);
    Result<string[]> ReadLines(string path);
    Result<string> ReadAllText(string path);
    Result<byte[]> ReadAllBytes(string path);
    Result Write(string path, string contents);
    Result Copy(string sourceFile, string destinationFile);

    /// <summary>Recursively delete a directory and everything under it.</summary>
    Result DeleteDirectory(string path);

    /// <summary>Watch a directory tree for changes to files matching <paramref name="filter"/>.</summary>
    Result<IFileWatcher> Watch(string path, string filter = "*.*");
}
