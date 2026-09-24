using Magic.Contexts;

namespace Magic.Interfaces;

/// <summary>
/// The pile of entities. Entities are <see cref="Handle"/> handles; their data are unmanaged component structs
/// (plain data, no references) that live in the ECS storage, never in managed objects.
/// Systems that run over the entities are scheduled through <see cref="Schedule"/>.
/// </summary>
public interface IWorld
{
    int EntityCount { get; }

    /// <summary>
    /// Creates an entity, optionally named and parented. Names must be unique among siblings.
    /// </summary>
    Handle Create(string? name = null, Handle parent = default);

    /// <summary>
    /// Creates <c>ids.Length</c> entities in one go and writes their handles into <paramref name="ids"/>.
    /// </summary>
    void Create(Span<Handle> ids);

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
    IWorldQueryBuilder<T1> Query<T1>() where T1 : unmanaged;

    /// <summary>
    /// Query the world.
    /// </summary>
    IWorldQueryBuilder<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged;

    /// <summary>
    /// Query the world.
    /// </summary>
    IWorldQueryBuilder<T1, T2, T3> Query<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;

    /// <summary>
    /// Query the world.
    /// </summary>
    IWorldQueryBuilder<T1, T2, T3, T4> Query<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged;

    /// <summary>
    /// Schedule a pipeline to run on the world.
    /// </summary>
    IWorldPipelineBuilder Schedule(string name);
}
