namespace Magic.Interfaces;

/// <summary>
/// A Core service told about every type of one kind (<see cref="Of"/>) in an assembly as it loads (the engine's own
/// first, then each gem's and the project's), and again as it unloads. Implemented as
/// <see cref="IRegisterFromGem{T}"/>, which names the kind, and added to the services under this contract, where
/// <see cref="Gems"/> finds every one and reflects each assembly once for all of them.
/// </summary>
internal interface IRegisterFromGem
{
    /// <summary>
    /// The kind of type registered: an attribute (the types that carry it) or a class or interface (the concrete
    /// types assignable to it, structs included).
    /// </summary>
    Type Of { get; }

    /// <summary>
    /// An instance to add to the services under <paramref name="type"/>, so constructors can ask for it, or null.
    /// </summary>
    object? Register(Type type);

    void Unregister(Type type);
}

/// <summary>
/// An <see cref="IRegisterFromGem"/> of the types of <typeparamref name="T"/>.
/// </summary>
internal interface IRegisterFromGem<T> : IRegisterFromGem
{
    Type IRegisterFromGem.Of => typeof(T);
}
