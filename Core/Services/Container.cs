namespace Magic.Services;

/// <summary>
/// The instances gem constructors can ask for, by contract type: the host's services and every gem export. A
/// contract can have several (every <see cref="Interfaces.ILogger"/>, every <see cref="Interfaces.IDebugUI"/>);
/// <see cref="Get{T}"/> answers the first one added, <see cref="All{T}"/> all of them in the order they came.
/// Main thread only: filled at startup and changed by <see cref="Gems"/> between frames.
/// </summary>
public sealed class Container
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

    /// <summary>Removes every contract <paramref name="instance"/> was added under.</summary>
    public void Remove(object instance)
    {
        _entries.RemoveAll(e => ReferenceEquals(e.Instance, instance));
    }

    public bool Has(Type contract)
    {
        return _entries.Exists(e => e.Contract == contract);
    }

    /// <summary>The first instance added under <paramref name="contract"/>, or null.</summary>
    public object? Get(Type contract)
    {
        return _entries.Find(e => e.Contract == contract).Instance;
    }

    public T? Get<T>() where T : class
    {
        return (T?)Get(typeof(T));
    }

    public T[] All<T>() where T : class
    {
        return [.. _entries.Where(e => e.Contract == typeof(T)).Select(e => (T)e.Instance)];
    }
}
