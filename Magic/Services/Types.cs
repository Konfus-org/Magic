using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// Every loaded type of <typeparamref name="T"/> (the engine's, the gems', the project's), looked up by name: how a
/// chunk file names a component (<see cref="Contexts.Components.IComponent"/>) and a script asset its class
/// (<see cref="IScript"/>). Kept current as gems load and unload; read from any thread.
/// </summary>
public sealed class Types<T> : IRegisterFromGem<T>
{
    private volatile Type[] _types = []; // replaced whole by the one thread gems change on, so a reader sees one set

    /// <summary>
    /// The types whose name or full name is <paramref name="name"/>, ignoring case: none, one, or several when two
    /// assemblies share a name.
    /// </summary>
    public IReadOnlyList<Type> Named(string name)
    {
        return [.. _types.Where(type => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase))];
    }

    object? IRegisterFromGem.Register(Type type)
    {
        _types = [.. _types, type];
        return null;
    }

    void IRegisterFromGem.Unregister(Type type)
    {
        _types = [.. _types.Where(registered => registered != type)];
    }
}
