using System.Diagnostics.CodeAnalysis;

namespace Magic.Interfaces;

/// <summary>
/// What the host and the loaded gems provide, by contract type: the host's services and every interface a gem
/// implements. For the few things that must look a service up by a type they only learn at runtime (an asset
/// loader by asset type, a script's constructor parameters); anything that knows what it needs takes that service
/// itself. A contract can have several instances (every <see cref="ILogger"/>, every <see cref="IScripting"/>):
/// <see cref="Get"/> answers the first one added, <see cref="All"/> all of them in the order they came.
/// <see cref="Add"/> and <see cref="Remove"/> are for the host and the gem loader, as gems come and go.
/// Main thread only: filled at startup and changed between frames.
/// </summary>
public interface IServices
{
    /// <summary>
    /// Adds <paramref name="instance"/> under <paramref name="contract"/>, after whatever is there already. Throws
    /// <see cref="ArgumentException"/> when it is not a <paramref name="contract"/>.
    /// </summary>
    void Add(Type contract, object instance);

    /// <summary>
    /// Removes <paramref name="instance"/> from <paramref name="contract"/>; nothing when it is not there.
    /// </summary>
    void Remove(Type contract, object instance);

    bool Has(Type contract);

    /// <summary>
    /// The first instance added under <paramref name="contract"/>, which is expected to be there: throws
    /// <see cref="InvalidOperationException"/> when nothing was. For what may be missing, <see cref="TryGet"/>.
    /// </summary>
    object Get(Type contract);

    /// <summary>
    /// The first instance added under <paramref name="contract"/>, for what may not be there: false, and null, when nothing was.
    /// </summary>
    bool TryGet(Type contract, [NotNullWhen(true)] out object? instance);

    /// <summary>
    /// Every instance added under <paramref name="contract"/>, in the order they came; empty when none was.
    /// </summary>
    IReadOnlyList<object> All(Type contract);
}
