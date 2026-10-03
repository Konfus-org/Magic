using System.Diagnostics.CodeAnalysis;

namespace Magic.Services;

/// <summary>
/// The instances gem constructors can ask for, by contract type: the host's services and every gem export. A
/// contract can have several (every <see cref="Interfaces.ILogger"/>, every <see cref="Interfaces.IDebugUI"/>);
/// <see cref="Get{T}"/> answers the first one added, <see cref="All{T}"/> all of them in the order they came.
/// Main thread only: filled at startup and changed by <see cref="Gems"/> between frames.
/// </summary>
internal sealed class Container
{
    private readonly List<(Type Contract, object Instance)> _entries = [];

    public void Add<T>(T instance) where T : class
    {
        Add(typeof(T), instance);
    }

    public void Add(Type contract, object instance)
    {
        if (!contract.IsInstanceOfType(instance))
            throw new ArgumentException($"{instance.GetType().FullName} is not a {contract.FullName}.", nameof(instance));

        _entries.Add((contract, instance));
    }

    /// <summary>
    /// Removes every contract <paramref name="instance"/> was added under.
    /// </summary>
    public void Remove(object instance)
    {
        _entries.RemoveAll(entry => ReferenceEquals(entry.Instance, instance));
    }

    public bool Has(Type contract)
    {
        return _entries.Exists(entry => entry.Contract == contract);
    }

    /// <summary>
    /// The first instance added under <paramref name="contract"/>, which is expected to be there: throws
    /// <see cref="InvalidOperationException"/> when nothing was. For what may be missing, <see cref="TryGet(Type, out object?)"/>.
    /// </summary>
    public object Get(Type contract)
    {
        return TryGet(contract, out object? instance)
            ? instance
            : throw new InvalidOperationException($"Nothing provides {contract.FullName}.");
    }

    /// <inheritdoc cref="Get(Type)"/>
    public T Get<T>() where T : class
    {
        return (T)Get(typeof(T));
    }

    /// <summary>
    /// The first instance added under <paramref name="contract"/>, for what may not be there: false, and null, when nothing was.
    /// </summary>
    public bool TryGet(Type contract, [NotNullWhen(true)] out object? instance)
    {
        instance = _entries.Find(entry => entry.Contract == contract).Instance;
        return instance is not null;
    }

    /// <inheritdoc cref="TryGet(Type, out object?)"/>
    public bool TryGet<T>([NotNullWhen(true)] out T? instance) where T : class
    {
        bool found = TryGet(typeof(T), out object? untyped);
        instance = (T?)untyped;
        return found;
    }

    public T[] All<T>() where T : class
    {
        return [.. _entries.Where(entry => entry.Contract == typeof(T)).Select(entry => (T)entry.Instance)];
    }
}
