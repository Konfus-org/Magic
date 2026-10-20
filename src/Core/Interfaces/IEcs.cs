using Magic.Contexts.ECS;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// The entity world: entities, the components they have, and the systems that run over them every frame, as a gem keeps
/// them. Core's <see cref="World"/> is built on it and hands it every call, so gems and game code take the world, and
/// only what the world is built on takes this. A component is an unmanaged struct that implements
/// <see cref="IComponent"/>, plain data with public fields, which the ECS stores in columns rather than in managed
/// objects.
/// <para>
/// The world is the main thread's (<see cref="EngineThread.Main"/>): every call is made there, or throws. Work on
/// another thread hands what it built to the main thread (<see cref="Threads.InvokeAsync{T}"/>), in a batch where it
/// can (<see cref="Create{T1}(ReadOnlySpan{T1})"/>), and queries it there too. The one exception is reading: while an
/// <see cref="IParallelSystem"/> runs, it may read other entities (<see cref="IsAlive"/>, <see cref="Has{T}"/>,
/// <see cref="Get{T}"/>) on whichever thread it is on.
/// </para>
/// <para>
/// Changing which components an entity has (<see cref="Create()"/>, <see cref="Destroy"/>, <see cref="Remove{T}"/>, or
/// <see cref="Set{T}"/> of one it lacks) moves it between columns, so it throws during a query's loop or a parallel
/// system: collect what to change, and change it after. Every call but <see cref="IsAlive"/> and <see cref="Has{T}"/>
/// throws for an entity that is not alive.
/// </para>
/// </summary>
public interface IEcs
{
    /// <summary>
    /// A new entity, with no components.
    /// </summary>
    Entity Create();

    /// <summary>
    /// One new entity per element of <paramref name="first"/>, each with the component at its index: a batch made in
    /// one go rather than one entity and one component at a time, for what was built on another thread. The span
    /// handed back is the ECS's own, good until the next batch is made: copy it to keep it.
    /// </summary>
    ReadOnlySpan<Entity> Create<T1>(ReadOnlySpan<T1> first)
        where T1 : unmanaged, IComponent;

    /// <summary>
    /// A new entity with <paramref name="components"/>, each of its own type, known only as the program runs: a prefab's
    /// part's, as a file names them. They go in at once, boxed, so it is slower than a typed batch; for spawning what a
    /// file holds, not for the frame loop.
    /// </summary>
    Entity Create(ReadOnlySpan<IComponent> components);

    /// <summary>
    /// <see cref="Create{T1}(ReadOnlySpan{T1})"/> with two components each; the spans are the same length.
    /// </summary>
    ReadOnlySpan<Entity> Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent;

    /// <summary>
    /// Destroys the entity and its components. Its slot goes to a later entity, with a new
    /// <see cref="Entity.Version"/>.
    /// </summary>
    void Destroy(Entity entity);

    bool IsAlive(Entity entity);

    /// <summary>
    /// Gives the entity <paramref name="component"/>, or overwrites the one it has.
    /// </summary>
    void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent;

    /// <summary>
    /// Gives the entity <paramref name="components"/>, each of its own type, known only as the program runs, as
    /// <see cref="Create(ReadOnlySpan{IComponent})"/> makes one with them: a saved entity's, put back. They go in at
    /// once, boxed, each overwriting the one of its type the entity has; for restoring what was saved, not for the
    /// frame loop.
    /// </summary>
    void Set(Entity entity, ReadOnlySpan<IComponent> components);

    /// <summary>
    /// The entity's components, each boxed: for saving the world, not the frame loop, which reads them through
    /// queries. Allocates.
    /// </summary>
    IComponent[] GetComponents(Entity entity);

    /// <summary>
    /// The entity's component by reference, so a write goes straight into storage. Good until the entity's components
    /// next change; throws when it has none.
    /// </summary>
    ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent;

    bool Has<T>(Entity entity)
        where T : unmanaged, IComponent;

    /// <summary>
    /// Takes the component off the entity; nothing happens when it has none.
    /// </summary>
    void Remove<T>(Entity entity)
        where T : unmanaged, IComponent;

    /// <summary>
    /// Every entity, chunk by chunk. A system is handed its query; this is for reading the world outside one. It
    /// allocates nothing.
    /// </summary>
    EcsQuery Query();

    /// <summary>
    /// The entities that have a <typeparamref name="T1"/>, chunk by chunk, with spans straight over storage: writing
    /// through them changes the components.
    /// </summary>
    EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Query{T1}"/> over the entities that have both components.
    /// </summary>
    EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Query{T1}"/> over the entities that have all three components.
    /// </summary>
    EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Query{T1}"/> over the entities that have all four components.
    /// </summary>
    EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent;

    /// <summary>
    /// Runs <paramref name="system"/> every frame, in its <see cref="ISystem.Phase"/>, after the systems added before
    /// it, until the subscription is disposed. A system that throws is logged and taken off; the others go on.
    /// </summary>
    Subscription Add(ISystem system);

    /// <summary>
    /// <see cref="Add(ISystem)"/> for a system over the entities that have a <typeparamref name="T1"/>.
    /// </summary>
    Subscription Add<T1>(ISystem<T1> system)
        where T1 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Add(ISystem)"/> for a system over the entities that have both components.
    /// </summary>
    Subscription Add<T1, T2>(ISystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Add(ISystem)"/> for a system over the entities that have all three components.
    /// </summary>
    Subscription Add<T1, T2, T3>(ISystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Add(ISystem)"/> for a system over the entities that have all four components.
    /// </summary>
    Subscription Add<T1, T2, T3, T4>(ISystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Add(ISystem)"/> for a system that runs a chunk at a time on the ECS's worker threads.
    /// </summary>
    Subscription Add(IParallelSystem system);

    /// <summary>
    /// <see cref="Add(IParallelSystem)"/> for a system over the entities that have a <typeparamref name="T1"/>.
    /// </summary>
    Subscription Add<T1>(IParallelSystem<T1> system)
        where T1 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Add(IParallelSystem)"/> for a system over the entities that have both components.
    /// </summary>
    Subscription Add<T1, T2>(IParallelSystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Add(IParallelSystem)"/> for a system over the entities that have all three components.
    /// </summary>
    Subscription Add<T1, T2, T3>(IParallelSystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Add(IParallelSystem)"/> for a system over the entities that have all four components.
    /// </summary>
    Subscription Add<T1, T2, T3, T4>(IParallelSystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent;
}
