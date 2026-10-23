using Magic.Contexts.ECS;
using Magic.Contexts.Frames;

namespace Magic.Interfaces;

/// <summary>
/// Work over every entity, which the ECS runs every frame on the main thread. A system that looks at certain components
/// says which (<see cref="ISystem{T1}"/> and on, up to four), and work over many entities is an
/// <see cref="IParallelSystem"/>, which runs on the ECS's threads. While the world is paused (<see cref="Frame.IsPaused"/>)
/// a system waits, unless its class is <see cref="Attributes.UnpausableAttribute">[Unpausable]</see>, as one that copies
/// what is drawn is.
/// </summary>
public interface ISystem
{
    /// <summary>
    /// The part of the frame it runs in: <see cref="Phase.Update"/>, <see cref="Phase.FixedUpdate"/> or
    /// <see cref="Phase.LateUpdate"/>, the main thread's. A system that renders copies what it draws in LateUpdate.
    /// </summary>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsQuery query);
}

/// <summary>
/// <see cref="ISystem"/> over the entities that have a <typeparamref name="T1"/>.
/// </summary>
public interface ISystem<T1>
    where T1 : unmanaged, IComponent
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsQuery<T1> query);
}

/// <summary>
/// <see cref="ISystem"/> over the entities that have both components.
/// </summary>
public interface ISystem<T1, T2>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsQuery<T1, T2> query);
}

/// <summary>
/// <see cref="ISystem"/> over the entities that have all three components.
/// </summary>
public interface ISystem<T1, T2, T3>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsQuery<T1, T2, T3> query);
}

/// <summary>
/// <see cref="ISystem"/> over the entities that have all four components.
/// </summary>
public interface ISystem<T1, T2, T3, T4>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
    where T4 : unmanaged, IComponent
{
    /// <inheritdoc cref="ISystem.Phase"/>
    Phase Phase => Phase.Update;

    void Run(in Frame frame, EcsQuery<T1, T2, T3, T4> query);
}
