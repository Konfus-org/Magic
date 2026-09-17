using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace Core.Contexts;

internal sealed class GemLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    // Passing isCollectible: true is critical for GC collection
    public GemLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    [RequiresUnreferencedCode("Calls System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath(String)")]
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Anything the host already has loaded (Core, DryIoc, ...) must be shared, never loaded
        // a second time into this context: types from two copies of the same assembly are not
        // interchangeable, and the gem would no longer implement the host's IGem.
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

        return null; // Fall back to the default context for host assemblies (like IGem)
    }
}
