namespace Magic.IntegrationTests;

/// <summary>
/// A folder of its own under the temp folder, gone again when disposed.
/// </summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A watcher or a worker still holds a file: the OS clears the temp folder.
        }
    }

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MagicTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Writes <paramref name="text"/> to a file under the folder, creating its subfolder; returns the full path.
    /// </summary>
    public string Write(string relative, string text)
    {
        string file = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file) ?? Path);
        File.WriteAllText(file, text);

        return file;
    }
}
