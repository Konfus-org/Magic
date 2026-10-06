using Magic.Interfaces;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Services;

/// <summary>
/// The instances gem constructors can ask for, by contract type: the host's services and every gem export, the one
/// <see cref="IServices"/> of the run, and used only as one (its members are the interface's, so the generic
/// extensions apply to it too). Main thread only: filled at startup and changed by <see cref="Gems"/> between frames.
/// </summary>
internal sealed class Container : IServices
{
    private readonly List<(Type Contract, object Instance)> _entries = [];

    void IServices.Add(Type contract, object instance)
    {
        if (!contract.IsInstanceOfType(instance))
            throw new ArgumentException($"{instance.GetType().FullName} is not a {contract.FullName}.", nameof(instance));

        _entries.Add((contract, instance));
    }

    void IServices.Remove(Type contract, object instance)
    {
        _entries.RemoveAll(entry => entry.Contract == contract && ReferenceEquals(entry.Instance, instance));
    }

    bool IServices.Has(Type contract)
    {
        return _entries.Exists(entry => entry.Contract == contract);
    }

    object IServices.Get(Type contract)
    {
        return ((IServices)this).TryGet(contract, out object? instance)
            ? instance
            : throw new InvalidOperationException($"Nothing provides {contract.FullName}.");
    }

    bool IServices.TryGet(Type contract, [NotNullWhen(true)] out object? instance)
    {
        instance = _entries.Find(entry => entry.Contract == contract).Instance;
        return instance is not null;
    }

    IReadOnlyList<object> IServices.All(Type contract)
    {
        return [.. _entries.Where(entry => entry.Contract == contract).Select(entry => entry.Instance)];
    }
}
