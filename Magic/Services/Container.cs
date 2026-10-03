using Magic.Interfaces;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Services;

/// <summary>
/// The instances gem constructors can ask for, by contract type: the host's services and every gem export. This is
/// the write side; everything that reads takes it as <see cref="IServices"/>. Main thread only: filled at startup
/// and changed by <see cref="Gems"/> between frames.
/// </summary>
internal sealed class Container : IServices
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

    public object Get(Type contract)
    {
        return TryGet(contract, out object? instance)
            ? instance
            : throw new InvalidOperationException($"Nothing provides {contract.FullName}.");
    }

    public bool TryGet(Type contract, [NotNullWhen(true)] out object? instance)
    {
        instance = _entries.Find(entry => entry.Contract == contract).Instance;
        return instance is not null;
    }

    public IReadOnlyList<object> All(Type contract)
    {
        return [.. _entries.Where(entry => entry.Contract == contract).Select(entry => entry.Instance)];
    }
}
