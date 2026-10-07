namespace Magic.Interfaces;

/// <summary>
/// A service told about every type of one kind (<see cref="Of"/>) in each assembly that loads (the engine's own, then
/// each gem's and the project's), and again as it unloads: how a gem learns of the settings, asset or component types
/// the gems after it bring. Implemented as <see cref="ITypeRegistrar{T}"/>, which names the kind. A gem whose class
/// implements it is one from the moment it is constructed, and is first told of every assembly already loaded; a gem
/// whose constructor asks for a type a registrar hands back (its settings) references that registrar's gem, so it
/// always loads after it.
/// </summary>
public interface ITypeRegistrar
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
/// An <see cref="ITypeRegistrar"/> of the types of <typeparamref name="T"/>.
/// </summary>
public interface ITypeRegistrar<T> : ITypeRegistrar
{
    Type ITypeRegistrar.Of => typeof(T);
}
