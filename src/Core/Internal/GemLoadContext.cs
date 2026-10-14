using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Magic.Interfaces;

namespace Magic.Internal;

/// <summary>
/// Where one gem's assemblies load: the gem, its NuGet dependencies and its native libraries, all from its folder.
/// What the gem does not carry (Magic, the framework) comes from the host, so a gem and Core share one
/// <see cref="IGem"/>, and two gems can depend on different versions of the same package. A native library is the
/// exception: it loads once, from the folder of the first gem that binds it, and every gem after shares that copy.
/// </summary>
internal sealed class GemLoadContext(string gemPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(gemPath))
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
            if (_natives.TryGetValue(unmanagedDllName, out nint loaded))
            {
                return loaded;
            }

            if (_resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is not { } path)
            {
                return 0;
            }

            nint handle = LoadUnmanagedDllFromPath(path);
            _natives[unmanagedDllName] = handle;
            return handle;
        }
    }
}
