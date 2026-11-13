using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Magic.Utils;

namespace Magic.Projects;

/// <summary>
/// The code loaded in the process, Core's and each gem's, by type name. Any thread.
/// </summary>
/// <remarks>
/// It finds the code that a file names, such as a component's struct.
/// </remarks>
public static class Assemblies
{
    private static ImmutableArray<Assembly> _assemblies = [typeof(Assemblies).Assembly];

    /// <summary>
    /// Replaced whole on the game thread, and read whole on any.
    /// </summary>
    private static FrozenDictionary<string, ImmutableArray<Type>> _byName = Index(_assemblies);

    /// <summary>
    /// Gets the one <typeparamref name="T"/> named <paramref name="name"/>, or why not when none or more than one is
    /// loaded.
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
                $"more than one {typeof(T).Name} is named {name}: "
                    + $"{string.Join(", ", matching.Select(type => type.FullName))}."
            ),
        };
    }

    /// <summary>
    /// Adds a gem's <paramref name="assembly"/> until the subscription is disposed. Game thread.
    /// </summary>
    /// <remarks>
    /// Dispose the subscription, or the gem's code cannot unload.
    /// </remarks>
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
    /// Indexes the top-level types of <paramref name="assemblies"/> by name.
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
        /// Gets what the assembly's build recorded as <paramref name="key"/>
        /// (<c>&lt;AssemblyMetadata Include="key"&gt;</c>); null when it is missing or empty.
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
        /// Gets each type of the assembly that loads.
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
