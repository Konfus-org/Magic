using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Magic.Utils;

namespace Magic.Projects;

/// <summary>
/// The code loaded in the process, Core's and each gem's, by the name of its types: how what a file names from code, such
/// as a component's struct, is found. Any thread.
/// </summary>
public static class Assemblies
{
    private static ImmutableArray<Assembly> _assemblies = [typeof(Assemblies).Assembly];

    /// <summary>
    /// Replaced whole on the main thread, and read whole on any.
    /// </summary>
    private static FrozenDictionary<string, ImmutableArray<Type>> _byName = Index(_assemblies);

    /// <summary>
    /// The one type named <paramref name="name"/> that is a <typeparamref name="T"/>; why not, when no code loaded
    /// declares one or more than one does.
    /// </summary>
    public static Result<Type> Get<T>(string name)
    {
        ImmutableArray<Type> named = Volatile.Read(ref _byName).GetValueOrDefault(name, []);
        ImmutableArray<Type> matching = named.RemoveAll(type => !typeof(T).IsAssignableFrom(type));

        return matching.Length switch
        {
            1 => Result.Success(matching[0]),
            0 => Result.Failure($"no code loaded declares a {typeof(T).Name} named {name}."),
            _ => Result.Failure(
                $"more than one {typeof(T).Name} is named {name}: {string.Join(", ", matching.Select(type => type.FullName))}."
            ),
        };
    }

    /// <summary>
    /// Adds a gem's <paramref name="assembly"/> until the subscription is disposed, which it must be for the gem's code to
    /// unload. Main thread.
    /// </summary>
    internal static Subscription Register(Assembly assembly)
    {
        _assemblies = _assemblies.Add(assembly);
        Volatile.Write(ref _byName, Index(_assemblies));

        return new Subscription(() =>
        {
            _assemblies = _assemblies.Remove(assembly);
            Volatile.Write(ref _byName, Index(_assemblies));
        });
    }

    /// <summary>
    /// The top-level types <paramref name="assemblies"/> hold, by their names.
    /// </summary>
    private static FrozenDictionary<string, ImmutableArray<Type>> Index(ImmutableArray<Assembly> assemblies)
    {
        Dictionary<string, ImmutableArray<Type>> byName = new(StringComparer.Ordinal);
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in assembly.GetLoadableTypes())
            {
                if (!type.IsNested)
                    byName[type.Name] = byName.GetValueOrDefault(type.Name, []).Add(type);
            }
        }

        return byName.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

public static class AssemblyExtensions
{
    extension(Assembly assembly)
    {
        /// <summary>
        /// What the assembly's build recorded as <paramref name="key"/> (<c>&lt;AssemblyMetadata Include="key"&gt;</c>);
        /// null when nothing, or an empty string, was.
        /// </summary>
        public string? GetMetadata(string key)
        {
            foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (
                    string.Equals(metadata.Key, key, StringComparison.Ordinal)
                    && metadata.Value is { Length: > 0 } value
                )
                    return value;
            }

            return null;
        }

        /// <summary>
        /// Every type the assembly holds; those that load, when some don't.
        /// </summary>
        internal Type[] GetLoadableTypes()
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return [.. ex.Types.OfType<Type>()];
            }
        }
    }
}
