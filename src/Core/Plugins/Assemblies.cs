using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Magic.Utils;

namespace Magic.Plugins;

/// <summary>
/// The code loaded for a run, Core's and each gem's, by the name of its types: how what a file names from code, such as a
/// component's struct, is found. Any thread.
/// </summary>
public sealed class Assemblies
{
    private ImmutableArray<Assembly> _assemblies;

    /// <summary>
    /// Replaced whole on the main thread, and read whole on any.
    /// </summary>
    private FrozenDictionary<string, ImmutableArray<Type>> _byName = FrozenDictionary<
        string,
        ImmutableArray<Type>
    >.Empty;

    internal Assemblies(Assembly core)
    {
        _assemblies = [core];
        Rebuild();
    }

    /// <summary>
    /// The one type named <paramref name="name"/> that is a <typeparamref name="T"/>; why not, when no code loaded
    /// declares one or more than one does.
    /// </summary>
    public Result<Type> Get<T>(string name)
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
    internal Subscription Register(Assembly assembly)
    {
        _assemblies = _assemblies.Add(assembly);
        Rebuild();

        return new Subscription(() =>
        {
            _assemblies = _assemblies.Remove(assembly);
            Rebuild();
        });
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

    private void Rebuild()
    {
        Dictionary<string, ImmutableArray<Type>> byName = new(StringComparer.Ordinal);
        foreach (Assembly assembly in _assemblies)
        {
            foreach (Type type in GetTypes(assembly))
            {
                if (!type.IsNested)
                    byName[type.Name] = byName.GetValueOrDefault(type.Name, []).Add(type);
            }
        }

        Volatile.Write(ref _byName, byName.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
