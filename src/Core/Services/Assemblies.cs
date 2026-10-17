using System.Collections.Concurrent;
using System.Reflection;
using Magic.Contexts.Utils;
using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// The code loaded: Magic's own assembly, and each gem's, the project's and the bundled scripts' as it loads, by the
/// path it was loaded from, so one loaded again, as the project's is when it is built while the engine runs, replaces
/// its last build. What a file names from code, a script's class or a component's struct, is found here by the name
/// of its type (<see cref="Get"/>), checked to be what the file is for (<see cref="Get{T}"/>): the assemblies are
/// searched once per name, what each resolved to is kept for the run, and forgotten when an assembly is added or
/// replaced. A gem asks for it as it asks for any service, as the one that reads <c>.cs</c> files does to find a
/// script's class. Safe from any thread.
/// </summary>
public sealed class Assemblies
{
    private readonly Lock _adding = new();
    private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase) { ["Magic"] = typeof(Assemblies).Assembly };

    /// <summary>
    /// Replaced whole as assemblies are added, so a reader on another thread sees one set.
    /// </summary>
    private volatile Assembly[] _all;

    /// <summary>
    /// What each name asked for resolved to, so a world of entities naming the same few types searches once each.
    /// </summary>
    private readonly ConcurrentDictionary<string, Result<Type>> _named = new(StringComparer.Ordinal);

    public Assemblies()
    {
        _all = [.. _loaded.Values];
    }

    /// <summary>
    /// Takes in <paramref name="assembly"/>, loaded from <paramref name="path"/>, in place of the one last loaded from
    /// there; nothing when it is that one, as a run started again hands it over once more.
    /// </summary>
    internal void Add(string path, Assembly assembly)
    {
        lock (_adding)
        {
            if (_loaded.TryGetValue(path, out Assembly? had) && had == assembly)
            {
                return;
            }

            _loaded[path] = assembly;
            _all = [.. _loaded.Values];
            _named.Clear();
        }
    }

    /// <summary>
    /// The one top-level type named <paramref name="name"/> in the code loaded: what a file of that name declares. Why
    /// not, when no code loaded declares one, or more than one does.
    /// </summary>
    public Result<Type> Get(string name)
    {
        return _named.GetOrAdd(name, Search);
    }

    /// <summary>
    /// <see cref="Get"/>, when the type is a <typeparamref name="T"/>: a component's struct for
    /// <see cref="IComponent"/>, a script's class for <see cref="IScript"/>. Why not, when it is not one, or no one
    /// type is named so.
    /// </summary>
    public Result<Type> Get<T>(string name)
    {
        Result<Type> type = Get(name);
        if (!type.Ok || type.Value.IsAssignableTo(typeof(T)))
        {
            return type;
        }

        return Result.Failure<Type>($"{name} is a {type.Value.FullName}, not a {typeof(T).Name}.");
    }

    /// <summary>
    /// Every type <paramref name="assembly"/> holds; those that load, when some of its do not.
    /// </summary>
    internal static IReadOnlyList<Type> From(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            List<Type> loaded = [];
            foreach (Type? type in ex.Types)
            {
                if (type is not null)
                {
                    loaded.Add(type);
                }
            }

            return loaded;
        }
    }

    private Result<Type> Search(string name)
    {
        List<Type> found = [];
        foreach (Assembly assembly in _all)
        {
            foreach (Type type in From(assembly))
            {
                if (!type.IsNested && !type.ContainsGenericParameters && string.Equals(type.Name, name, StringComparison.Ordinal))
                {
                    found.Add(type);
                }
            }
        }

        return found.Count switch
        {
            0 => Result.Failure<Type>($"no code loaded declares a type named {name}: the project's code, or the gem that compiles it, is to be built, or the gem listed."),
            1 => Result.Success(found[0]),
            _ => Result.Failure<Type>($"{found.Count} types are named {name} ({string.Join(", ", found.ConvertAll(type => type.FullName))}); a type a file names has a name of its own."),
        };
    }
}
