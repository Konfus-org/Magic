using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// The code loaded: Core's types, and every type of the project's code and of each gem as it loads, before any gem is
/// made, so whatever finds a class by its name finds every one: a file names a component by its type's name
/// (<see cref="Component"/>), and a script by the class named like its file. Code loaded again, as the project's is
/// when it is built while the engine runs, replaces what its last build held. Safe from any thread.
/// </summary>
public static class Code
{
    private static readonly Lock _adding = new();

    /// <summary>
    /// The types of each assembly taken in, by the path it was loaded from, Core's by its name: one loaded again
    /// replaces its own.
    /// </summary>
    private static readonly Dictionary<string, Type[]> _loaded = new(StringComparer.OrdinalIgnoreCase) { ["Magic"] = typeof(Code).Assembly.GetTypes() };

    /// <summary>
    /// Replaced whole as code is added, so a reader on another thread sees one set. A name two components share maps to
    /// null: a file names either by its full name.
    /// </summary>
    private static volatile Dictionary<string, Type?> _components = ComponentsOf(_loaded.Values, new Dictionary<string, Type?>(StringComparer.Ordinal));
    private static volatile Type[] _types = TypesOf(_loaded.Values);

    /// <summary>
    /// Every type of the code loaded, as it is now: one array, replaced whole as code is added or loaded again, so hold
    /// the one read while walking it.
    /// </summary>
    public static IReadOnlyList<Type> Types => _types;

    /// <summary>
    /// The component (<see cref="IComponent"/>) named <paramref name="name"/>, its type's name or, when two share that,
    /// its full name; null when no component has it.
    /// </summary>
    public static Type? Component(string name)
    {
        return _components.TryGetValue(name, out Type? type) ? type : null;
    }

    /// <summary>
    /// Takes in <paramref name="types"/>, those of the assembly loaded from <paramref name="path"/>, in place of those
    /// of its last load; nothing when they are the ones it has, as a run started again hands them over once more.
    /// </summary>
    internal static void Add(string path, Type[] types)
    {
        lock (_adding)
        {
            if (_loaded.TryGetValue(path, out Type[]? had) && had == types)
            {
                return;
            }

            bool isAgain = had is not null;
            _loaded[path] = types;
            _types = TypesOf(_loaded.Values);
            _components = isAgain
                ? ComponentsOf(_loaded.Values, new Dictionary<string, Type?>(StringComparer.Ordinal))
                : ComponentsOf([types], new Dictionary<string, Type?>(_components, StringComparer.Ordinal));
        }
    }

    private static Type[] TypesOf(Dictionary<string, Type[]>.ValueCollection loaded)
    {
        List<Type> types = [];
        foreach (Type[] set in loaded)
        {
            types.AddRange(set);
        }

        return [.. types];
    }

    /// <summary>
    /// <paramref name="named"/> with the components among <paramref name="loaded"/> added, each by its name and its
    /// full name.
    /// </summary>
    private static Dictionary<string, Type?> ComponentsOf(IEnumerable<Type[]> loaded, Dictionary<string, Type?> named)
    {
        foreach (Type[] set in loaded)
        {
            foreach (Type type in set)
            {
                if (!type.IsValueType || type.ContainsGenericParameters || !type.IsAssignableTo(typeof(IComponent)))
                {
                    continue;
                }

                Name(type, type.Name);
                Name(type, type.FullName ?? type.Name);
            }
        }

        return named;

        void Name(Type type, string name)
        {
            if (!named.TryGetValue(name, out Type? other))
            {
                named[name] = type;
                return;
            }

            if (other is not null && other != type)
            {
                Debugging.Warn($"{other.FullName} and {type.FullName} are both components named {name}: a file names either by its full name.");
                named[name] = null;
            }
        }
    }
}
