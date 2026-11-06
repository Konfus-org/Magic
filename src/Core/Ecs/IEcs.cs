namespace Magic.Ecs;

/// <summary>
/// The entity world: entities and the components they have, as a gem keeps them. A component is an unmanaged struct
/// that implements <see cref="IComponent"/>, which the ECS stores in columns rather than in managed objects.
/// <para>
/// Work over many entities is a system: a function over chunks, the entities stored together, which a gem calls from
/// its own hook, through <see cref="Query()"/> on its own thread, or through <see cref="Run{TState}"/> on the ECS's
/// worker threads.
/// </para>
/// <para>
/// The world is the thread's that made the gem providing it: every call is made there, or throws. Work on another
/// thread hands what it built to that thread as data. The one exception is reading: while a <see cref="Run{TState}"/>
/// runs, it may read other entities (<see cref="IsAlive"/>, <see cref="Has{T}"/>, <see cref="Get{T}"/>) on whichever
/// thread it is on.
/// </para>
/// <para>
/// Changing which components an entity has (<see cref="Create()"/>, <see cref="Destroy"/>, <see cref="Remove{T}"/>, or
/// <see cref="Set{T}"/> of one it lacks) moves it between columns, so it throws during a query's loop or a run; make
/// it after. Every call but <see cref="IsAlive"/> and <see cref="Has{T}"/> throws for an entity that is not alive.
/// </para>
/// </summary>
public interface IEcs
{
    /// <summary>
    /// A new entity, with no components.
    /// </summary>
    Entity Create();

    /// <summary>
    /// A new entity with <paramref name="components"/>, each of its own type: what a file or a save made, known only as
    /// the program runs. It boxes.
    /// </summary>
    Entity Create(ReadOnlySpan<IComponent> components);

    /// <summary>
    /// Destroys the entity and its components. Its slot goes to a later entity, with a new
    /// <see cref="Entity.Version"/>.
    /// </summary>
    void Destroy(Entity entity);

    bool IsAlive(Entity entity);

    /// <summary>
    /// Copies of every component the entity has, boxed.
    /// </summary>
    IComponent[] GetComponents(Entity entity);

    /// <summary>
    /// Gives the entity <paramref name="component"/>, or overwrites the one it has.
    /// </summary>
    void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent;

    /// <summary>
    /// Gives the entity each of <paramref name="components"/>, each of its own type, or overwrites the one it has. It
    /// boxes, as making one from them does.
    /// </summary>
    void Set(Entity entity, ReadOnlySpan<IComponent> components);

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
    /// Every entity, chunk by chunk, on this thread. It allocates nothing.
    /// </summary>
    EcsQuery Query();

    /// <summary>
    /// The entities that have a <typeparamref name="T1"/>, chunk by chunk, on this thread, with spans straight over
    /// storage: writing through them changes the components.
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
    /// Runs <paramref name="run"/> on every chunk of entities, spread over the ECS's worker threads, one chunk each,
    /// and returns once every chunk is done. <paramref name="state"/> is what each run is handed, such as the frame,
    /// so <paramref name="run"/> can be a delegate made once rather than a closure. A run writes only through its
    /// chunk and adds up anything shared with <see cref="Interlocked"/>. What a chunk throws is thrown here, once the
    /// others are done.
    /// </summary>
    void Run<TState>(in TState state, EcsChunkRun<TState, EcsChunk> run);

    /// <summary>
    /// <see cref="Run{TState}"/> over the entities that have a <typeparamref name="T1"/>.
    /// </summary>
    void Run<TState, T1>(in TState state, EcsChunkRun<TState, EcsChunk<T1>> run)
        where T1 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Run{TState}"/> over the entities that have both components.
    /// </summary>
    void Run<TState, T1, T2>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Run{TState}"/> over the entities that have all three components.
    /// </summary>
    void Run<TState, T1, T2, T3>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2, T3>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Run{TState}"/> over the entities that have all four components.
    /// </summary>
    void Run<TState, T1, T2, T3, T4>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2, T3, T4>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent;
}
