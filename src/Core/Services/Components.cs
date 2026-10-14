using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// Every component type (<see cref="IComponent"/>), Core's and the loaded gems', by its name and by its full name: how a
/// file names one. Core takes in a gem's as it loads the gem, before any gem is made, so whatever a gem starts reading as
/// it is made finds every gem's. Safe from any thread.
/// </summary>
public static class Components
{
    private static readonly Lock _adding = new();

    /// <summary>
    /// Replaced whole as a gem's are added, so a reader on another thread sees one set. A name two types share maps to
    /// null: a file names either by its full name.
    /// </summary>
    private static volatile Dictionary<string, Type?> _named = WithComponents(typeof(Components).Assembly.GetTypes(), new(StringComparer.Ordinal));

    /// <summary>
    /// The component named <paramref name="name"/>, its type's name or, when two share that, its full name; null when no
    /// component has it.
    /// </summary>
    public static Type? Get(string name)
    {
        return _named.TryGetValue(name, out Type? type) ? type : null;
    }

    /// <summary>
    /// Takes in the components among a gem's <paramref name="types"/>.
    /// </summary>
    internal static void Add(IReadOnlyList<Type> types)
    {
        lock (_adding)
        {
            _named = WithComponents(types, new Dictionary<string, Type?>(_named, StringComparer.Ordinal));
        }
    }

    private static Dictionary<string, Type?> WithComponents(IReadOnlyList<Type> types, Dictionary<string, Type?> named)
    {
        foreach (Type type in types)
        {
            if (!type.IsValueType || type.ContainsGenericParameters || !type.IsAssignableTo(typeof(IComponent)))
            {
                continue;
            }

            Name(type, type.Name);
            Name(type, type.FullName ?? type.Name);
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
