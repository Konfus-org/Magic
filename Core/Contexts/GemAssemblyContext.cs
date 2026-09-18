using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace Magic.Contexts;

internal sealed class GemAssemblyContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    // Passing isCollectible: true is critical for GC collection
    public GemAssemblyContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    [RequiresUnreferencedCode("Calls System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath(String)")]
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Anything the host already has loaded (Core, DryIoc, ...) must be shared, never loaded
        // a second time into this context: types from two copies of the same assembly are not
        // interchangeable, and the gem's [Gem] attribute would not be the host's GemAttribute.
        if (Default.Assemblies.Any(a => a.GetName().Name == assemblyName.Name))
        {
            return null;
        }

        // Resolve the gem's own dependencies (like shared libraries or framework dlls)
        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null; // Fall back to the default context for host assemblies (like Magic itself)
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
