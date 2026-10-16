namespace Magic.Interfaces;

/// <summary>
/// A script that is a system (<see cref="ISystem"/>): a prefab attaches it to an entity of its own, and it runs over
/// every entity while that one lives. The entity its constructor is given holds it and is no entity it runs over.
/// </summary>
public interface ISystemScript : ISystem, IScript;

/// <summary>
/// <see cref="ISystemScript"/> over the entities that have a <typeparamref name="T1"/>.
/// </summary>
public interface ISystemScript<T1> : ISystem<T1>, IScript
    where T1 : unmanaged, IComponent;

/// <summary>
/// <see cref="ISystemScript"/> over the entities that have both components.
/// </summary>
public interface ISystemScript<T1, T2> : ISystem<T1, T2>, IScript
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent;

/// <summary>
/// <see cref="ISystemScript"/> over the entities that have all three components.
/// </summary>
public interface ISystemScript<T1, T2, T3> : ISystem<T1, T2, T3>, IScript
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent;

/// <summary>
/// <see cref="ISystemScript"/> over the entities that have all four components.
/// </summary>
public interface ISystemScript<T1, T2, T3, T4> : ISystem<T1, T2, T3, T4>, IScript
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
    where T4 : unmanaged, IComponent;
