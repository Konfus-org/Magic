using Magic.Contexts;

namespace Magic.Interfaces;

/// <summary>
/// What happened to a component, for <see cref="IEcs.Observe{T}"/>.
/// </summary>
public enum ComponentEvent : byte
{
    /// <summary>
    /// The component was added to an entity (before any value was set).
    /// </summary>
    Added,

    /// <summary>
    /// A value was set, including the first one.
    /// </summary>
    Set,

    /// <summary>
    /// The component is about to go, because it is being removed or its entity destroyed; it can still be read.
    /// </summary>
    Removed
}

/// <summary>
/// The pile of entities. Entities are <see cref="Handle"/> handles; their data are unmanaged component structs
/// (plain data, no references) that live in the ECS storage, never in managed objects.
/// Systems that run over the entities are scheduled through <see cref="Schedule"/>.
/// </summary>
public interface IEcs
{
    int EntityCount { get; }

    /// <summary>
    /// Creates an entity, optionally named and parented. Names must be unique among siblings.
    /// </summary>
    Handle Create(string? name = null, Handle parent = default);

    /// <summary>
    /// Groups the entities made until the answer is disposed, so they are made together: the ones given the same
    /// parent and the same components are inserted in one go, their values copied side by side, instead of each
    /// being moved once for every component it is given. The way to make many entities, a chunk's say. Inside the
    /// group the calls are the usual ones (<see cref="Create"/>, <see cref="Set{T}"/>,
    /// <see cref="Add{T}"/>), and the handles they answer are good at once, as parents too; but what an entity made
    /// in the group was given cannot be read back (<see cref="Get{T}"/>, <see cref="Has{T}"/>, <see cref="Lookup"/>,
    /// a query), and no observer hears of it, until the group ends. Only entities made in the group are held back:
    /// any other entity changes at once, as always. Removing from, renaming, reparenting or destroying an entity
    /// made in the group ends the holding back early, for all of them. Groups may be nested; the outermost
    /// applies. Main thread.
    /// <code>
    /// using (ecs.Group())
    /// {
    ///     Handle entity = ecs.Create(parent: root);
    ///     ecs.Set(entity, transform);
    ///     ecs.Set(entity, renderer);
    /// }
    /// </code>
    /// </summary>
    IDisposable Group();

    /// <summary>
    /// Destroys the entity and all of its children.
    /// </summary>
    void Destroy(Handle entity);

    bool IsAlive(Handle entity);

    /// <summary>
    /// A disabled entity keeps its data but is skipped by every query and system until re-enabled.
    /// </summary>
    void Enable(Handle entity, bool enabled = true);

    bool IsEnabled(Handle entity);

    string? GetName(Handle entity);

    void SetName(Handle entity, string? name);

    /// <summary>
    /// Finds an entity by path, like <c>"Level.Player"</c>. Returns <see cref="Handle.None"/> if there is none.
    /// </summary>
    Handle Lookup(string path);

    /// <summary>
    /// The parent, or <see cref="Handle.None"/> for a root entity.
    /// </summary>
    Handle GetParent(Handle entity);

    /// <summary>
    /// Re-parents the entity; <see cref="Handle.None"/> makes it a root entity.
    /// </summary>
    void SetParent(Handle entity, Handle parent);

    Handle[] GetChildren(Handle parent);

    /// <summary>
    /// Adds or overwrites the component.
    /// </summary>
    void Set<T>(Handle entity, in T value) where T : unmanaged;

    /// <summary>
    /// The component by reference, so writes go straight into storage. Throws if the entity lacks it.
    /// The reference is only valid until the next structural change (Add/Remove/Create/Destroy/SetParent).
    /// </summary>
    ref T Get<T>(Handle entity) where T : unmanaged;

    bool TryGet<T>(Handle entity, out T value) where T : unmanaged;

    bool Has<T>(Handle entity) where T : unmanaged;

    /// <summary>
    /// Adds a tag (an empty struct) or a default-valued component. No-op if already present.
    /// </summary>
    void Add<T>(Handle entity) where T : unmanaged;

    void Remove<T>(Handle entity) where T : unmanaged;

    /// <summary>
    /// The entity that carries every singleton component. Use the normal component API on it to set, check or remove
    /// one.
    /// </summary>
    Handle Singleton();

    /// <summary>
    /// The singleton by reference; created with default values on first use.
    /// </summary>
    ref T Singleton<T>() where T : unmanaged;

    /// <summary>
    /// Query the world.
    /// </summary>
    IEcsQueryBuilder<T1> Query<T1>() where T1 : unmanaged;

    /// <summary>
    /// Query the world.
    /// </summary>
    IEcsQueryBuilder<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged;

    /// <summary>
    /// Query the world.
    /// </summary>
    IEcsQueryBuilder<T1, T2, T3> Query<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;

    /// <summary>
    /// Query the world.
    /// </summary>
    IEcsQueryBuilder<T1, T2, T3, T4> Query<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged;

    /// <summary>
    /// Schedule a pipeline to run on the world.
    /// </summary>
    IEcsPipelineBuilder Schedule(string name);

    /// <summary>
    /// Calls <paramref name="handler"/> whenever <paramref name="e"/> happens to a <typeparamref name="T"/>,
    /// synchronously, on the thread doing it. The handler may read the entity but must not change the world
    /// structurally; queue that for later. Dispose to stop. Perf-driven, flagged: it is here so that a
    /// system that mirrors entities elsewhere (the renderer) pays for changes, not for a sweep every frame.
    /// </summary>
    IDisposable Observe<T>(ComponentEvent e, Action<Handle> handler) where T : unmanaged;
}
