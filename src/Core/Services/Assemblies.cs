using System.Collections.Concurrent;
using System.Reflection;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// The code loaded: Magic's own assembly, and each gem's, the project's and the bundled scripts' as it loads, by the
/// path it was loaded from, so one loaded again, as the project's is when it is built while the engine runs, replaces
/// its last build. What a file names from code, a script's class or a component's struct, is found here by the name of
/// its type, checked to be what the file is for (<see cref="TryGet{T}"/>): the assemblies are searched once per name,
/// what each resolved to is kept for the run, and forgotten when an assembly is added, replaced or let go. A gem asks
/// for it as it asks for any service, as the one that reads <c>.cs</c> files does to find a script's class. Safe from
/// any thread.
/// </summary>
public sealed class Assemblies
{
    private readonly Lock _adding = new();
    private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Magic"] = typeof(Assemblies).Assembly
    };

    /// <summary>
    /// The assemblies loaded and what each name resolved to among them, replaced whole as assemblies are added, so a
    /// reader on another thread sees one set, and a search that ends after one is added lands in the set it searched,
    /// which nothing reads any more.
    /// </summary>
    private volatile Code _code;

    public Assemblies()
    {
        _code = new Code([.. _loaded.Values]);
    }

    /// <summary>
    /// The one top-level type named <paramref name="name"/> in the code loaded, what a file of that name declares, when
    /// it is a <typeparamref name="T"/>: a component's struct for <see cref="IComponent"/>, a script's class for
    /// <see cref="IScript"/>. Why not, when it is not one, when no code loaded declares one, or more than one does.
    /// </summary>
    public Result<Type> TryGet<T>(string name)
    {
        Result<Type> type = TryGet(name);

        if (!type.Ok || type.Value.IsAssignableTo(typeof(T))) return type;

        return Result.Failure<Type>($"{name} is a {type.Value.FullName}, not a {typeof(T).Name}.");
    }

    /// <summary>
    /// Every type <paramref name="assembly"/> holds; those that load, when some of its do not.
    /// </summary>
    internal static IReadOnlyList<Type> GetAllFrom(Assembly assembly)
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
                if (type is not null) loaded.Add(type);
            }

            return loaded;
        }
    }

    /// <summary>
    /// Takes in <paramref name="assembly"/>, loaded from <paramref name="path"/>, in place of the one last loaded from
    /// there, until the subscription is disposed: then it is let go, unless one loaded from there since has taken its
    /// place.
    /// </summary>
    internal Subscription Register(string path, Assembly assembly)
    {
        lock (_adding)
        {
            _loaded[path] = assembly;
            _code = new Code([.. _loaded.Values]);
        }

        return new Subscription(() => Unregister(path, assembly));
    }

    private static Result<Type> Search(string name, IReadOnlyList<Assembly> all)
    {
        List<Type> found = [];
        foreach (Assembly assembly in all)
        {
            foreach (Type type in GetAllFrom(assembly))
            {
                if (!type.IsNested && !type.ContainsGenericParameters
                    && string.Equals(type.Name, name, StringComparison.Ordinal))
                {
                    found.Add(type);
                }
            }
        }

        return found.Count switch
        {
            0 => Result.Failure<Type>($"no code loaded declares a type named {name}: the project's code, "
                + "or the gem that compiles it, is to be built, or the gem listed."),
            1 => Result.Success(found[0]),
            _ => Result.Failure<Type>($"{found.Count} types are named {name} "
                + $"({string.Join(", ", found.ConvertAll(type => type.FullName))}); "
                + "a type a file names has a name of its own."),
        };
    }

    /// <summary>
    /// The one top-level type named <paramref name="name"/> in the code loaded, whatever it is.
    /// </summary>
    private Result<Type> TryGet(string name)
    {
        Code code = _code;

        return code.Named.GetOrAdd(name, Search, code.All);
    }

    /// <summary>
    /// Lets go of <paramref name="assembly"/>, loaded from <paramref name="path"/>, while it is still the one from
    /// there.
    /// </summary>
    private void Unregister(string path, Assembly assembly)
    {
        lock (_adding)
        {
            if (!_loaded.TryGetValue(path, out Assembly? loaded) || loaded != assembly) return;

            _loaded.Remove(path);
            _code = new Code([.. _loaded.Values]);
        }
    }

    /// <summary>
    /// The assemblies loaded as of one add, and what each name asked for resolved to among them, so a world of entities
    /// naming the same few types searches once each.
    /// </summary>
    private sealed class Code(Assembly[] all)
    {
        public IReadOnlyList<Assembly> All { get; } = all;

        public ConcurrentDictionary<string, Result<Type>> Named { get; } = new(StringComparer.Ordinal);
    }
}
