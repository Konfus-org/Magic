using System.Collections;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Arch.Core;
using Magic.Ecs;
using Magic.Projects;
using Magic.Threading;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using Entity = Magic.Ecs.Entity;

namespace Magic.ArchEcs;

/// <summary>
/// The entity world, in one Arch world, on the game thread. Entities are Arch's, bit for bit, and
/// components live in Arch's chunks, so a query hands out spans straight over them and allocates nothing. Arch trusts
/// its caller, so this gem checks what Arch would let go wrong silently (a dead entity, a structural change during a
/// loop, a call from another thread) and throws instead.
/// </summary>
public sealed class ArchEcsGem : IGem, IEcs, IDisposable
{
    private readonly ArchWorld _world;

    /// <summary>
    /// Which thread is the game thread, the only one that may change the world.
    /// </summary>
    private readonly Threads _threads;

    /// <summary>
    /// One walk per query, by the type of chunk it hands out, made the first time the query is asked for and reused.
    /// </summary>
    private readonly Dictionary<Type, object> _queryWalks = [];

    /// <summary>
    /// When each chunk was last written, by archetype then by the chunk's index in it: the version written then, none
    /// for a chunk never written.
    /// </summary>
    private readonly Dictionary<Archetype, List<long>> _written = [];

    /// <summary>
    /// How many query loops are open: while any is, an entity's components can't change, as that would move it out
    /// from under their spans.
    /// </summary>
    private int _openLoops;

    /// <summary>
    /// The version a write stamps its chunk with, moved on by each read query so what is written after one is newer.
    /// </summary>
    private long _version = 1;

    public ArchEcsGem(Threads threads)
    {
        // An entity becomes Arch's and back by reinterpreting its bits, so an Arch that laid its own out differently
        // would scramble every handle.
        ArchEntity probe = Unsafe.BitCast<Entity, ArchEntity>(new Entity(1, 2, 3));
        if (probe.Id != 1 || probe.WorldId != 2 || probe.Version != 3)
        {
            throw new InvalidOperationException(
                $"Arch lays out its entity as {probe}, not as Magic's (Id, EcsWorld, Version)."
            );
        }

        _threads = threads;
        _world = ArchWorld.Create();
    }

    public void Dispose() => _world.Dispose();

    public Entity Create(ReadOnlySpan<IComponent> components)
    {
        ThrowIfCannotChange();
        ComponentType[] types = new ComponentType[components.Length];
        object[] values = new object[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            types[i] = GetComponentType(components[i].GetType());
            values[i] = components[i];
        }

        ArchEntity created = _world.Create(types);
        _world.SetRange(created, values);
        Stamp(created);

        return Unsafe.BitCast<ArchEntity, Entity>(created);
    }

    public IComponent[] GetComponents(Entity entity) =>
        [.. _world.GetAllComponents(GetAlive(entity)).Cast<IComponent>()];

    public void Destroy(Entity entity)
    {
        ThrowIfCannotChange();
        ArchEntity alive = GetAlive(entity);

        // The archetype's last entity moves into its place.
        Stamp(alive);
        _world.Destroy(alive);
    }

    public bool IsAlive(Entity entity)
    {
        ThrowIfNotWorldThread();

        return _world.IsAlive(Unsafe.BitCast<Entity, ArchEntity>(entity));
    }

    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent
    {
        ThrowIfNotWorldThread();
        ArchEntity alive = GetAlive(entity);
        ref T existing = ref _world.TryGetRef<T>(alive, out bool has);
        if (has)
        {
            // Not structural, so it is fine inside a loop.
            existing = component;
            Stamp(alive);

            return;
        }

        ThrowIfCannotChange();
        Stamp(alive);
        _world.Add(alive, component);
        Stamp(alive);
    }

    public void Set(Entity entity, ReadOnlySpan<IComponent> components)
    {
        ThrowIfNotWorldThread();
        ArchEntity alive = GetAlive(entity);
        Stamp(alive);
        foreach (IComponent component in components)
        {
            object[] value = [component];
            if (_world.Has(alive, GetComponentType(component.GetType())))
            {
                // Not structural, so it is fine inside a loop.
                _world.SetRange(alive, value);
                continue;
            }

            ThrowIfCannotChange();
            _world.AddRange(alive, value);
        }

        Stamp(alive);
    }

    public ref readonly T Get<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfNotWorldThread();
        ref T component = ref _world.TryGetRef<T>(GetAlive(entity), out bool has);
        if (!has)
            throw new InvalidOperationException($"{entity} has no {typeof(T).Name}.");

        return ref component;
    }

    public ref T Mutate<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfNotWorldThread();
        ArchEntity alive = GetAlive(entity);
        ref T component = ref _world.TryGetRef<T>(alive, out bool has);
        if (!has)
            throw new InvalidOperationException($"{entity} has no {typeof(T).Name}.");

        Stamp(alive);

        return ref component;
    }

    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent => IsAlive(entity) && _world.Has<T>(Unsafe.BitCast<Entity, ArchEntity>(entity));

    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfNotWorldThread();
        ArchEntity alive = GetAlive(entity);
        if (!_world.Has<T>(alive))
            return;

        ThrowIfCannotChange();
        Stamp(alive);
        _world.Remove<T>(alive);
        Stamp(alive);
    }

    public EcsQuery Query() => new(GetWalk(All.Description, static (chunk, _) => All.Cut(chunk)));

    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent => new(GetWalk(All<T1>.Description, All<T1>.Cut), _version++);

    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => new(GetWalk(All<T1, T2>.Description, All<T1, T2>.Cut), _version++);

    public EcsMutation<T1> Mutate<T1>()
        where T1 : unmanaged, IComponent
    {
        IEnumerable<EcsMutableChunk<T1>> walk = GetWalk(
            All<T1>.Description,
            static (chunk, _) => All<T1>.CutMutable(chunk)
        );
        Stamp(All<T1>.Description);

        return new(walk);
    }

    public EcsMutation<T1, T2> Mutate<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        IEnumerable<EcsMutableChunk<T1, T2>> walk = GetWalk(
            All<T1, T2>.Description,
            static (chunk, _) => All<T1, T2>.CutMutable(chunk)
        );
        Stamp(All<T1, T2>.Description);

        return new(walk);
    }

    /// <summary>
    /// A chunk's live entities as Magic's: Arch's arrays run to the chunk's capacity, past its last entity.
    /// </summary>
    private static ReadOnlySpan<Entity> GetEntities(Chunk chunk) =>
        MemoryMarshal.Cast<ArchEntity, Entity>(chunk.Entities.AsSpan(0, chunk.Count));

    private static Span<T> GetColumn<T>(Chunk chunk) => chunk.GetArray<T>().AsSpan(0, chunk.Count);

    /// <summary>
    /// The ECS's type of <paramref name="type"/>, a component known only as the program runs, from its typed registration
    /// so a struct is measured as the typed calls measure it.
    /// </summary>
    private static ComponentType GetComponentType(Type type)
    {
        if (ComponentRegistry.TryGet(type, out ComponentType registered))
            return registered;

        return
            typeof(Component<>).MakeGenericType(type).GetField(nameof(Component<int>.ComponentType))?.GetValue(null)
                is ComponentType found
            ? found
            : throw new InvalidOperationException($"The ECS has no type for {type.Name}.");
    }

    private IEnumerable<TChunk> GetWalk<TChunk>(in QueryDescription description, Func<Chunk, long, TChunk> cut)
        where TChunk : allows ref struct
    {
        ThrowIfNotWorldThread();
        ref object? walk = ref CollectionsMarshal.GetValueRefOrAddDefault(_queryWalks, typeof(TChunk), out _);
        walk ??= new Walk<TChunk>(this, description, cut);

        return (Walk<TChunk>)walk;
    }

    private long GetWritten(Archetype archetype, int chunk) =>
        _written.TryGetValue(archetype, out List<long>? stamps) && chunk < stamps.Count ? stamps[chunk] : 0;

    /// <summary>
    /// Marks the chunk <paramref name="entity"/> is in as written now.
    /// </summary>
    private void Stamp(ArchEntity entity)
    {
        EntityData data = _world.GetEntityData(entity);
        Stamp(data.Archetype, data.Slot.ChunkIndex);
    }

    /// <summary>
    /// Marks every chunk <paramref name="description"/> matches as written now.
    /// </summary>
    private void Stamp(in QueryDescription description)
    {
        foreach (Archetype archetype in _world.Query(in description).GetArchetypeIterator())
        {
            for (int index = 0; index < archetype.ChunkCount; index++)
            {
                Stamp(archetype, index);
            }
        }
    }

    private void Stamp(Archetype archetype, int chunk)
    {
        ref List<long>? stamps = ref CollectionsMarshal.GetValueRefOrAddDefault(_written, archetype, out _);
        stamps ??= [];
        if (stamps.Count <= chunk)
            CollectionsMarshal.SetCount(stamps, chunk + 1);

        stamps[chunk] = _version;
    }

    private ArchEntity GetAlive(Entity entity)
    {
        ArchEntity arch = Unsafe.BitCast<Entity, ArchEntity>(entity);
        if (!_world.IsAlive(arch))
            throw new InvalidOperationException($"{entity} is not alive.");

        return arch;
    }

    private void ThrowIfNotWorldThread()
    {
        if (_threads.IsCurrent(EngineThread.Game))
            return;

        throw new InvalidOperationException(
            "The entity world is the game thread's: hand it what another thread built as data."
        );
    }

    /// <summary>
    /// Changing which components an entity has is the world's thread's, and never while a loop is open.
    /// </summary>
    private void ThrowIfCannotChange()
    {
        ThrowIfNotWorldThread();
        if (_openLoops == 0)
            return;

        throw new InvalidOperationException(
            "An entity's components cannot change during a query's loop: fill a list of your own and apply it after."
        );
    }

    /// <summary>
    /// The query over every entity, described once, and how a chunk is cut down to its entities.
    /// </summary>
    private static class All
    {
        internal static readonly QueryDescription Description = new();

        internal static EcsChunk Cut(Chunk chunk) => new(GetEntities(chunk));
    }

    /// <summary>
    /// The query over the entities with a <typeparamref name="T1"/>, described once, and how a chunk is cut down to
    /// those components.
    /// </summary>
    private static class All<T1>
        where T1 : unmanaged, IComponent
    {
        internal static readonly QueryDescription Description = new QueryDescription().WithAll<T1>();

        internal static EcsChunk<T1> Cut(Chunk chunk, long written) =>
            new(GetEntities(chunk), GetColumn<T1>(chunk), written);

        internal static EcsMutableChunk<T1> CutMutable(Chunk chunk) => new(GetEntities(chunk), GetColumn<T1>(chunk));
    }

    /// <summary>
    /// <see cref="All{T1}"/> with both components.
    /// </summary>
    private static class All<T1, T2>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        internal static readonly QueryDescription Description = new QueryDescription().WithAll<T1, T2>();

        internal static EcsChunk<T1, T2> Cut(Chunk chunk, long written) =>
            new(GetEntities(chunk), GetColumn<T1>(chunk), GetColumn<T2>(chunk), written);

        internal static EcsMutableChunk<T1, T2> CutMutable(Chunk chunk) =>
            new(GetEntities(chunk), GetColumn<T1>(chunk), GetColumn<T2>(chunk));
    }

    /// <summary>
    /// A query's walk over its chunks, gathered as a loop starts, which stay current as nothing can move an entity
    /// until the loop ends. One per query and reused, so a loop allocates nothing; a loop over the same query started
    /// inside one gets a walk of its own.
    /// </summary>
    private sealed class Walk<TChunk>(ArchEcsGem ecs, QueryDescription description, Func<Chunk, long, TChunk> cut)
        : IEnumerable<TChunk>,
            IEnumerator<TChunk>
        where TChunk : allows ref struct
    {
        private readonly List<Chunk> _chunks = [];
        private readonly List<long> _written = [];
        private int _index;
        private bool _isWalking;

        public TChunk Current => cut(_chunks[_index], _written[_index]);

        object IEnumerator.Current =>
            throw new NotSupportedException("A chunk is spans over storage, which cannot be boxed.");

        public void Dispose()
        {
            _isWalking = false;
            ecs._openLoops--;
        }

        public IEnumerator<TChunk> GetEnumerator()
        {
            Walk<TChunk> walk = _isWalking ? new Walk<TChunk>(ecs, description, cut) : this;
            walk.Start();

            return walk;
        }

        public bool MoveNext() => ++_index < _chunks.Count;

        public void Reset() =>
            throw new NotSupportedException("A walk runs once per loop: start another loop to walk again.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void Start()
        {
            _chunks.Clear();
            _written.Clear();
            foreach (Archetype archetype in ecs._world.Query(in description).GetArchetypeIterator())
            {
                for (int index = 0; index < archetype.ChunkCount; index++)
                {
                    _chunks.Add(archetype.GetChunk(index));
                    _written.Add(ecs.GetWritten(archetype, index));
                }
            }

            _index = -1;
            _isWalking = true;
            ecs._openLoops++;
        }
    }
}
