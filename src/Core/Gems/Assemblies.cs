using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Magic.Utils;

namespace Magic.Gems;

/// <summary>
/// The code loaded for a run, Core's and each gem's, by the name of its types: how what a file names from code, such as a
/// component's struct, is found. Any thread.
/// </summary>
public sealed class Assemblies
{
    private readonly FrozenDictionary<string, ImmutableArray<Type>> _byName;

    internal Assemblies(IEnumerable<Assembly> assemblies)
    {
        Dictionary<string, ImmutableArray<Type>> byName = new(StringComparer.Ordinal);
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in GetTypes(assembly))
            {
                if (!type.IsNested)
                    byName[type.Name] = byName.GetValueOrDefault(type.Name, []).Add(type);
            }
        }

        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// The one type named <paramref name="name"/> that is a <typeparamref name="T"/>; why not, when no code loaded
    /// declares one or more than one does.
    /// </summary>
    public Result<Type> Get<T>(string name)
    {
        ImmutableArray<Type> named = _byName.GetValueOrDefault(name, []);
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
    /// Every type <paramref name="assembly"/> holds; those that load, when some don't.
    /// </summary>
    private static Type[] GetTypes(Assembly assembly)
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
