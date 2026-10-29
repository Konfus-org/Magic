using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.Host;

/// <summary>
/// Where one gem's assemblies load: the gem and its NuGet and native dependencies, from its folder. Magic and the
/// framework come from the host, so every gem shares one <see cref="IGem"/>, and two gems can carry different versions
/// of one package. A native library is found through the gem's dependency file, else under its
/// <c>runtimes/&lt;rid&gt;/native/</c>.
/// </summary>
internal sealed class GemLoadContext(string gemPath, IFileSystem files)
    : AssemblyLoadContext(Path.GetFileNameWithoutExtension(gemPath))
{
    /// <summary>
    /// Native libraries by name, loaded once for every gem: two copies would each keep their own state, so a window one
    /// opened would mean nothing to the other.
    /// </summary>
    private static readonly Dictionary<string, nint> _natives = new(StringComparer.Ordinal);
    private static readonly Lock _loadingNative = new();

    private readonly AssemblyDependencyResolver _resolver = new(gemPath);

    /// <summary>
    /// The gem's assembly this context was made for.
    /// </summary>
    public string GemPath => gemPath;

    protected override Assembly? Load(AssemblyName assemblyName) =>
        _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        lock (_loadingNative)
        {
            if (_natives.TryGetValue(unmanagedDllName, out nint loaded))
                return loaded;

            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) ?? FindCarried(unmanagedDllName);
            if (path is null)
                return 0;

            Log.Verbose($"Native library {unmanagedDllName} loads from {path}.");
            nint handle = LoadUnmanagedDllFromPath(path);
            _natives[unmanagedDllName] = handle;

            return handle;
        }
    }

    /// <summary>
    /// The file under the gem's <c>runtimes/&lt;rid&gt;/native/</c> named like the library, with or without Unix's
    /// <c>lib</c> prefix; null when there is none.
    /// </summary>
    private string? FindCarried(string name)
    {
        string folder = files.Combine(
            files.GetParent(gemPath),
            "runtimes",
            RuntimeInformation.RuntimeIdentifier,
            "native"
        );
        if (!files.DirectoryExists(folder))
            return null;

        Result<string[]> found = files.EnumerateFiles(folder, "*", recursive: false);
        if (!found.Ok)
            return null;

        foreach (string file in found.Value)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (
                string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "lib" + name, StringComparison.OrdinalIgnoreCase)
            )
            {
                return file;
            }
        }

        return null;
    }
}
