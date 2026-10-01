using Magic.Contexts.Assets;
using Magic.Interfaces;
using System.Reflection;
using System.Runtime.Loader;

namespace CSharpScriptingGem;

/// <summary>
/// Loads <see cref="Script"/> assets written in C#. It compiles nothing: the project's own build did, and the host
/// loaded the result. Loading a script finds its class, the one <see cref="IScript"/> named like the file
/// (<c>OrbitCamera.cs</c> declares <c>OrbitCamera</c>), among the assemblies loaded now. It keeps none of them, so
/// a project reloads freely under it.
/// </summary>
internal sealed class CSharpScripting : IGem, IAssetLoader<Script>
{
    public void Load(Script asset, byte[] bytes)
    {
        Type[] found = [.. Scripts().Where(type => type.Name == asset.Name)];

        asset.Type = found.Length switch
        {
            1 => found[0],
            0 => throw new InvalidOperationException($"no loaded assembly has an IScript class named {asset.Name}; build the project."),
            _ => throw new InvalidOperationException($"{found.Length} IScript classes are named {asset.Name} ({string.Join(", ", found.Select(type => type.FullName))}).")
        };
    }

    /// <summary>
    /// Every script class there is. Only what references the host can declare one, and an assembly being unloaded is
    /// no longer in a context that is listed, so a reloaded project's old classes are not found beside the new.
    /// </summary>
    private static IEnumerable<Type> Scripts()
    {
        string? host = typeof(IScript).Assembly.GetName().Name;
        foreach (Assembly assembly in AssemblyLoadContext.All.SelectMany(context => context.Assemblies))
        {
            if (assembly.IsDynamic || !assembly.GetReferencedAssemblies().Any(reference => reference.Name == host))
                continue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.OfType<Type>()];
            }

            foreach (Type type in types)
            {
                if (type.IsClass && !type.IsAbstract && typeof(IScript).IsAssignableFrom(type))
                    yield return type;
            }
        }
    }
}
