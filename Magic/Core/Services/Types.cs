using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// Every loaded type of <typeparamref name="T"/> (the gems', the project's), looked up by name: how a chunk file names a
/// component and a script asset its class. A gem that needs them keeps one and registers through it (it implements
/// <see cref="ITypeRegistrar{T}"/> by handing on to this). Kept current as gems load and unload; read from any thread.
/// </summary>
public sealed class Types<T> : ITypeRegistrar<T>
{
    private volatile Type[] _types = []; // replaced whole by the one thread gems change on, so a reader sees one set

    /// <summary>
    /// The types whose name or full name is <paramref name="name"/>, ignoring case: none, one, or several when two
    /// assemblies share a name.
    /// </summary>
    public IReadOnlyList<Type> Named(string name)
    {
        return [.. _types.Where(type => type.IsNamed(name))];
    }

    object? ITypeRegistrar.Register(Type type)
    {
        _types = [.. _types, type];
        return null;
    }

    void ITypeRegistrar.Unregister(Type type)
    {
        _types = [.. _types.Where(registered => registered != type)];
    }
}
