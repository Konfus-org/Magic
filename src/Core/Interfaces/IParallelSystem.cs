using Magic.Contexts.ECS;
using Magic.Contexts.Frames;

namespace Magic.Interfaces;

/// <summary>
/// Work over every entity, which the ECS runs every frame a chunk at a time on its worker threads, going on to the next
/// system once every chunk is done. <see cref="Run"/> runs on several threads at once, one chunk each, so it writes only
/// through its chunk and adds up anything shared with <see cref="Interlocked"/>; it may read other entities
/// (<see cref="IEcs.IsAlive"/>, <see cref="IEcs.Has{T}"/>, <see cref="IEcs.Get{T}"/>). What it throws is thrown on the
/// main thread once the other chunks are done, and the system is taken off as any other. A system that looks at certain
/// components says which (<see cref="IParallelSystem{T1}"/> and on, up to four).
/// </summary>
public interface IParallelSystem
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsChunk chunk);
}

/// <summary>
/// <see cref="IParallelSystem"/> over the entities that have a <typeparamref name="T1"/>.
/// </summary>
public interface IParallelSystem<T1>
    where T1 : unmanaged, IComponent
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsChunk<T1> chunk);
}

/// <summary>
/// <see cref="IParallelSystem"/> over the entities that have both components.
/// </summary>
public interface IParallelSystem<T1, T2>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsChunk<T1, T2> chunk);
}

/// <summary>
/// <see cref="IParallelSystem"/> over the entities that have all three components.
/// </summary>
public interface IParallelSystem<T1, T2, T3>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsChunk<T1, T2, T3> chunk);
}

/// <summary>
/// <see cref="IParallelSystem"/> over the entities that have all four components.
/// </summary>
public interface IParallelSystem<T1, T2, T3, T4>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
    where T4 : unmanaged, IComponent
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsChunk<T1, T2, T3, T4> chunk);
}
