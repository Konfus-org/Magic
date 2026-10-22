using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// Where one gem's assemblies load: the gem, its NuGet dependencies and its native libraries, all from its folder.
/// What the gem does not carry (Magic, the framework) comes from the host, so a gem and Core share one
/// <see cref="IGem"/>, and two gems can depend on different versions of the same package. A native library is the
/// exception: it loads once, from the folder of the first gem that binds it, and every gem after shares that copy. One
/// is found by the gem's dependency file, which lists what its packages brought, or else under the gem's
/// <c>runtimes/&lt;rid&gt;/native/</c>, where the build puts the binaries a gem declares itself.
/// </summary>
internal sealed class GemLoadContext(
    string gemPath,
    IFileSystem files) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(gemPath))
{
    /// <summary>
    /// Native libraries by the name gems bind them by. Two copies of one library would each keep state of their own: a
    /// window one copy opened would mean nothing to the other. Directory.Packages.props pins one version of each
    /// package, so every gem that binds a name expects the same library.
    /// </summary>
    private static readonly Dictionary<string, nint> _natives = new(StringComparer.Ordinal);
    private static readonly Lock _loadingNative = new();

    private readonly AssemblyDependencyResolver _resolver = new(gemPath);

    protected override Assembly? Load(AssemblyName assemblyName)
        => _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        lock (_loadingNative)
        {
            if (_natives.TryGetValue(unmanagedDllName, out nint loaded)) return loaded;

            if ((_resolver.ResolveUnmanagedDllToPath(unmanagedDllName)
                ?? FindCarried(unmanagedDllName)) is not { } path)
            {
                return 0;
            }

            Debugging.Verbose($"Native library {unmanagedDllName} loads from {path}.");
            nint handle = LoadUnmanagedDllFromPath(path);
            _natives[unmanagedDllName] = handle;

            return handle;
        }
    }

    /// <summary>
    /// The binary for this machine the gem carries under <c>runtimes/&lt;rid&gt;/native/</c>: the file named like
    /// the library, with or without the <c>lib</c> Unix puts in front. Null when the gem carries none.
    /// </summary>
    private string? FindCarried(string name)
    {
        string folder = files.Combine(
            files.GetParent(gemPath),
            "runtimes",
            RuntimeInformation.RuntimeIdentifier,
            "native");

        if (!files.FolderExists(folder)) return null;

        foreach (string file in files.Find(folder, ""))
        {
            string stem = Path.GetFileNameWithoutExtension(file);

            if (string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "lib" + name, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }
}
