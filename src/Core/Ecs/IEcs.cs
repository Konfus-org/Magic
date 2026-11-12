namespace Magic.Ecs;

/// <summary>
/// The entity world: the entities and the components that they have.
/// </summary>
/// <remarks>
/// <para>
/// A component is an unmanaged struct that implements <see cref="IComponent"/>. The ECS stores components in columns,
/// not in managed objects.
/// </para>
/// <para>
/// Work over many entities is a system: a function over chunks, the entities stored together, which a gem calls from
/// its own hook through <see cref="Query()"/>.
/// </para>
/// <para>
/// A call reads or writes. <see cref="Query{T1}()"/> and <see cref="Get{T}"/> read, through read-only spans and
/// references. <see cref="Mutate{T1}()"/>, <see cref="Mutate{T}(Entity)"/>, <see cref="Set{T}"/> and the calls that
/// change which components an entity has write. They mark each chunk that they give out or change as written, also
/// when nothing is written. A reader that keeps the <see cref="EcsQuery{T1}.Version"/> of its query knows that a chunk
/// whose <see cref="EcsChunk{T1}.Written"/> is not higher did not change since.
/// </para>
/// <para>
/// Make each call on the thread that made the gem that gives the world, or the call throws. Work on another thread
/// gives what it made to that thread as data.
/// </para>
/// <para>
/// A change to which components an entity has (<see cref="Create(ReadOnlySpan{IComponent})"/>, <see cref="Destroy"/>,
/// <see cref="Remove{T}"/>, or <see cref="Set{T}"/> of a component it does not have) moves it between columns. Such a
/// change throws during the loop of a query, so make it after the loop. Each call but <see cref="IsAlive"/> and
/// <see cref="Has{T}"/> throws for an entity that is not alive.
/// </para>
/// </remarks>
public interface IEcs
{
    /// <summary>
    /// Creates an entity with <paramref name="components"/>, one of each type.
    /// </summary>
    /// <remarks>
    /// Use it for components that are known only at run time, such as from a file or a save. It boxes.
    /// </remarks>
    Entity Create(ReadOnlySpan<IComponent> components);

    /// <summary>
    /// Destroys the entity and its components.
    /// </summary>
    /// <remarks>
    /// A later entity gets its slot, with a new <see cref="Entity.Version"/>.
    /// </remarks>
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
    /// Gives the entity each of <paramref name="components"/>, one of each type, or overwrites the one it has.
    /// </summary>
    /// <remarks>
    /// It boxes.
    /// </remarks>
    void Set(Entity entity, ReadOnlySpan<IComponent> components);

    /// <summary>
    /// Returns a reference to the component of the entity, to read.
    /// </summary>
    /// <remarks>
    /// The reference is good until the components of the entity change. It throws when the entity has no such
    /// component.
    /// </remarks>
    ref readonly T Get<T>(Entity entity)
        where T : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Get{T}"/> to write: a write goes straight into storage, and the entity's chunk is marked written.
    /// </summary>
    ref T Mutate<T>(Entity entity)
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
    /// The entities that have a <typeparamref name="T1"/>, chunk by chunk, on this thread, with read-only spans
    /// straight over storage.
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
    /// <see cref="Query{T1}()"/> to write: writing through the spans changes the components, and every chunk the loop
    /// walks is marked written.
    /// </summary>
    EcsMutation<T1> Mutate<T1>()
        where T1 : unmanaged, IComponent;

    /// <summary>
    /// <see cref="Mutate{T1}()"/> over the entities that have both components.
    /// </summary>
    EcsMutation<T1, T2> Mutate<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent;
}
