namespace Magic.Interfaces;

/// <summary>
/// A script that is a parallel system (<see cref="IParallelSystem"/>): a prefab attaches it to an entity of its own,
/// and it runs over every entity, on the ECS's threads, while that one lives. The entity its constructor is given holds
/// it and is no entity it runs over.
/// </summary>
public interface IParallelSystemScript : IParallelSystem, IScript;

/// <summary>
/// <see cref="IParallelSystemScript"/> over the entities that have a <typeparamref name="T1"/>.
/// </summary>
public interface IParallelSystemScript<T1> : IParallelSystem<T1>, IScript
    where T1 : unmanaged, IComponent;

/// <summary>
/// <see cref="IParallelSystemScript"/> over the entities that have both components.
/// </summary>
public interface IParallelSystemScript<T1, T2> : IParallelSystem<T1, T2>, IScript
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent;

/// <summary>
/// <see cref="IParallelSystemScript"/> over the entities that have all three components.
/// </summary>
public interface IParallelSystemScript<T1, T2, T3> : IParallelSystem<T1, T2, T3>, IScript
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent;

/// <summary>
/// <see cref="IParallelSystemScript"/> over the entities that have all four components.
/// </summary>
public interface IParallelSystemScript<T1, T2, T3, T4> : IParallelSystem<T1, T2, T3, T4>, IScript
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
    where T4 : unmanaged, IComponent;
