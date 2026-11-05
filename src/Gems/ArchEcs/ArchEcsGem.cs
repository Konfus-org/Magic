using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Arch.Core;
using Magic.Ecs;
using Magic.Projects;
using Schedulers;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using Entity = Magic.Ecs.Entity;

namespace Magic.ArchEcs;

/// <summary>
/// The entity world, in one Arch world, on the thread that made the gem. Entities are Arch's, bit for bit, and
/// components live in Arch's chunks, so a query hands out spans straight over them and allocates nothing; a run spreads
/// its chunks over Arch's job threads. Arch trusts its caller, so this gem checks what Arch would let go wrong silently
/// (a dead entity, a structural change during a loop, a call from another thread) and throws instead. It is public
/// because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class ArchEcsGem : IGem, IEcs, IDisposable
{
    private const string CannotChangeInLoop =
        "An entity's components cannot change during a query's loop or a run: fill a list of your own and apply it "
        + "after.";

    private readonly ArchWorld _world;

    /// <summary>
    /// The thread that made the gem, the only one that may change the world.
    /// </summary>
    private readonly int _worldThreadId;

    /// <summary>
    /// Arch schedules only from the thread that made its job threads, so they are made with the gem and ended with it.
    /// </summary>
    private readonly JobScheduler _jobScheduler;

    /// <summary>
    /// One walk per query, by the type of chunk it hands out, made the first time the query is asked for and reused.
    /// </summary>
    private readonly Dictionary<Type, object> _queryWalks = [];

    /// <summary>
    /// How many query loops and runs are open: while any is, an entity's components can't change, as that would move
    /// it out from under their spans, and the world may be read from a run's job threads.
    /// </summary>
    private volatile int _openLoops;

    /// <summary>
    /// What a run's chunk threw first, thrown on the world's thread once every chunk is done.
    /// </summary>
    private Exception? _firstRunError;

    public ArchEcsGem()
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

        _worldThreadId = Environment.CurrentManagedThreadId;
        _world = ArchWorld.Create();

        // The world's thread and whatever else the engine runs keep their cores; the jobs take the rest.
        _jobScheduler = new JobScheduler(
            new JobScheduler.Config
            {
                ThreadPrefixName = "Arch job ",
                ThreadCount = Math.Max(1, Environment.ProcessorCount - 2),
            }
        );
        ArchWorld.SharedJobScheduler = _jobScheduler;
    }

    public void Dispose()
    {
        ArchWorld.SharedJobScheduler = null;
        _jobScheduler.Dispose();
        _world.Dispose();
    }

    public Entity Create()
    {
        ThrowIfCannotChange();

        return Unsafe.BitCast<ArchEntity, Entity>(_world.Create());
    }

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

        return Unsafe.BitCast<ArchEntity, Entity>(created);
    }

    public IComponent[] GetComponents(Entity entity)
    {
        object?[] held = _world.GetAllComponents(GetAlive(entity));
        IComponent[] components = new IComponent[held.Length];
        for (int i = 0; i < held.Length; i++)
        {
            components[i] =
                held[i] as IComponent
                ?? throw new InvalidOperationException(
                    $"{entity} holds a {held[i]?.GetType().Name ?? "null"}, which is no component."
                );
        }

        return components;
    }

    public void Create<T1>(ReadOnlySpan<T1> first, Span<Entity> created)
        where T1 : unmanaged, IComponent
    {
        Span<ArchEntity> made = CreateBatch(created, first.Length, All<T1>.Signature);
        for (int i = 0; i < made.Length; i++)
        {
            _world.Set(made[i], in first[i]);
        }
    }

    public void Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, Span<Entity> created)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        if (second.Length != first.Length)
        {
            throw new ArgumentException(
                $"A batch's spans are the same length: {first.Length} {typeof(T1).Name}, "
                    + $"{second.Length} {typeof(T2).Name}.",
                nameof(second)
            );
        }

        Span<ArchEntity> made = CreateBatch(created, first.Length, All<T1, T2>.Signature);
        for (int i = 0; i < made.Length; i++)
        {
            _world.Set(made[i], in first[i]);
            _world.Set(made[i], in second[i]);
        }
    }

    public void Destroy(Entity entity)
    {
        ThrowIfCannotChange();
        _world.Destroy(GetAlive(entity));
    }

    public bool IsAlive(Entity entity)
    {
        ThrowIfCannotRead();

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

            return;
        }

        ThrowIfCannotChange();
        _world.Add(alive, component);
    }

    public void Set(Entity entity, ReadOnlySpan<IComponent> components)
    {
        ThrowIfNotWorldThread();
        ArchEntity alive = GetAlive(entity);
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
    }

    public ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfCannotRead();
        ref T component = ref _world.TryGetRef<T>(GetAlive(entity), out bool has);
        if (!has)
            throw new InvalidOperationException($"{entity} has no {typeof(T).Name}.");

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
        _world.Remove<T>(alive);
    }

    public EcsQuery Query() => new(GetWalk(All.Description, All.Cut));

    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent => new(GetWalk(All<T1>.Description, All<T1>.Cut));

    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => new(GetWalk(All<T1, T2>.Description, All<T1, T2>.Cut));

    public EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => new(GetWalk(All<T1, T2, T3>.Description, All<T1, T2, T3>.Cut));

    public EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => new(GetWalk(All<T1, T2, T3, T4>.Description, All<T1, T2, T3, T4>.Cut));

    public void Run<TState>(in TState state, EcsChunkRun<TState, EcsChunk> run) =>
        Run(All.Description, new ChunkJob<TState, EcsChunk>(this, state, All.Cut, run));

    public void Run<TState, T1>(in TState state, EcsChunkRun<TState, EcsChunk<T1>> run)
        where T1 : unmanaged, IComponent =>
        Run(All<T1>.Description, new ChunkJob<TState, EcsChunk<T1>>(this, state, All<T1>.Cut, run));

    public void Run<TState, T1, T2>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent =>
        Run(All<T1, T2>.Description, new ChunkJob<TState, EcsChunk<T1, T2>>(this, state, All<T1, T2>.Cut, run));

    public void Run<TState, T1, T2, T3>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2, T3>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent =>
        Run(
            All<T1, T2, T3>.Description,
            new ChunkJob<TState, EcsChunk<T1, T2, T3>>(this, state, All<T1, T2, T3>.Cut, run)
        );

    public void Run<TState, T1, T2, T3, T4>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2, T3, T4>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent =>
        Run(
            All<T1, T2, T3, T4>.Description,
            new ChunkJob<TState, EcsChunk<T1, T2, T3, T4>>(this, state, All<T1, T2, T3, T4>.Cut, run)
        );

    /// <summary>
    /// A chunk's live entities as Magic's: Arch's arrays run to the chunk's capacity, past its last entity.
    /// </summary>
    private static ReadOnlySpan<Entity> GetEntities(Chunk chunk) =>
        MemoryMarshal.Cast<ArchEntity, Entity>(chunk.Entities.AsSpan(0, chunk.Count));

    private static Span<T> GetComponents<T>(Chunk chunk) => chunk.GetArray<T>().AsSpan(0, chunk.Count);

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

    private IEnumerable<TChunk> GetWalk<TChunk>(in QueryDescription description, Func<Chunk, TChunk> cut)
        where TChunk : allows ref struct
    {
        ThrowIfNotWorldThread();
        ref object? walk = ref CollectionsMarshal.GetValueRefOrAddDefault(_queryWalks, typeof(TChunk), out _);
        walk ??= new Walk<TChunk>(this, description, cut);

        return (Walk<TChunk>)walk;
    }

    /// <summary>
    /// <paramref name="count"/> new entities of <paramref name="signature"/>, made straight into
    /// <paramref name="created"/>, as Arch's.
    /// </summary>
    private Span<ArchEntity> CreateBatch(Span<Entity> created, int count, in Signature signature)
    {
        if (created.Length != count)
        {
            throw new ArgumentException(
                $"A batch makes one entity per component: {count} components, room for {created.Length} entities.",
                nameof(created)
            );
        }

        ThrowIfCannotChange();
        Span<ArchEntity> made = MemoryMarshal.Cast<Entity, ArchEntity>(created);
        if (count > 0)
            _world.Create(made, in signature, count);

        return made;
    }

    /// <summary>
    /// Runs <paramref name="job"/> on every chunk <paramref name="description"/> matches, on Arch's job threads, and
    /// throws what a chunk threw once they are all done.
    /// </summary>
    private void Run<TJob>(in QueryDescription description, in TJob job)
        where TJob : struct, IChunkJob
    {
        ThrowIfNotWorldThread();
        _openLoops++;
        try
        {
            _world.InlineParallelChunkQuery(in description, in job);
        }
        finally
        {
            _openLoops--;
        }

        if (Interlocked.Exchange(ref _firstRunError, null) is { } thrown)
            ExceptionDispatchInfo.Throw(thrown);
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
        if (Environment.CurrentManagedThreadId == _worldThreadId)
            return;

        throw new InvalidOperationException(
            "The entity world is used from the thread that made it: hand it what another thread built as data."
        );
    }

    /// <summary>
    /// Reading is the world's thread's too, but during a run, on whichever thread a chunk runs.
    /// </summary>
    private void ThrowIfCannotRead()
    {
        if (_openLoops == 0)
            ThrowIfNotWorldThread();
    }

    /// <summary>
    /// Changing which components an entity has is the world's thread's, and never while a loop is open.
    /// </summary>
    private void ThrowIfCannotChange()
    {
        ThrowIfNotWorldThread();
        if (_openLoops == 0)
            return;

        throw new InvalidOperationException(CannotChangeInLoop);
    }

    /// <summary>
    /// A run over one Arch chunk on one of Arch's job threads, cut down to the components the run looks at. What it
    /// throws is kept for the world's thread, rather than ending the job thread and the process with it.
    /// </summary>
    private readonly struct ChunkJob<TState, TChunk>(
        ArchEcsGem ecs,
        TState state,
        Func<Chunk, TChunk> cut,
        EcsChunkRun<TState, TChunk> run
    ) : IChunkJob
        where TChunk : allows ref struct
    {
        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "What a chunk throws is thrown on the world's thread once the run is done."
        )]
        public void Execute(ref Chunk chunk)
        {
            try
            {
                run(in state, cut(chunk));
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref ecs._firstRunError, ex, null);
            }
        }
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
    /// The query over the entities with a <typeparamref name="T1"/> and the signature of an entity with just that,
    /// described once, and how a chunk is cut down to those components.
    /// </summary>
    private static class All<T1>
        where T1 : unmanaged, IComponent
    {
        internal static readonly QueryDescription Description = new QueryDescription().WithAll<T1>();

        internal static readonly Signature Signature = new(Component<T1>.ComponentType);

        internal static EcsChunk<T1> Cut(Chunk chunk) => new(GetEntities(chunk), GetComponents<T1>(chunk));
    }

    /// <summary>
    /// <see cref="All{T1}"/> with both components.
    /// </summary>
    private static class All<T1, T2>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        internal static readonly QueryDescription Description = new QueryDescription().WithAll<T1, T2>();

        internal static readonly Signature Signature = new(Component<T1>.ComponentType, Component<T2>.ComponentType);

        internal static EcsChunk<T1, T2> Cut(Chunk chunk) =>
            new(GetEntities(chunk), GetComponents<T1>(chunk), GetComponents<T2>(chunk));
    }

    /// <summary>
    /// <see cref="All{T1}"/> with all three components; no batch makes them, so it has no signature.
    /// </summary>
    private static class All<T1, T2, T3>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        internal static readonly QueryDescription Description = new QueryDescription().WithAll<T1, T2, T3>();

        internal static EcsChunk<T1, T2, T3> Cut(Chunk chunk) =>
            new(GetEntities(chunk), GetComponents<T1>(chunk), GetComponents<T2>(chunk), GetComponents<T3>(chunk));
    }

    /// <summary>
    /// <see cref="All{T1, T2, T3}"/> with all four components.
    /// </summary>
    private static class All<T1, T2, T3, T4>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        internal static readonly QueryDescription Description = new QueryDescription().WithAll<T1, T2, T3, T4>();

        internal static EcsChunk<T1, T2, T3, T4> Cut(Chunk chunk) =>
            new(
                GetEntities(chunk),
                GetComponents<T1>(chunk),
                GetComponents<T2>(chunk),
                GetComponents<T3>(chunk),
                GetComponents<T4>(chunk)
            );
    }

    /// <summary>
    /// A query's walk over its chunks, gathered as a loop starts, which stay current as nothing can move an entity
    /// until the loop ends. One per query and reused, so a loop allocates nothing; a loop over the same query started
    /// inside one gets a walk of its own.
    /// </summary>
    private sealed class Walk<TChunk>(ArchEcsGem ecs, QueryDescription description, Func<Chunk, TChunk> cut)
        : IEnumerable<TChunk>,
            IEnumerator<TChunk>
        where TChunk : allows ref struct
    {
        private readonly List<Chunk> _chunks = [];
        private int _index;
        private bool _isWalking;

        public TChunk Current => cut(_chunks[_index]);

        object IEnumerator.Current =>
            throw new NotSupportedException("A chunk is spans over storage, which cannot be boxed.");

        public void Dispose()
        {
            if (!_isWalking)
                return;

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
            foreach (ref Chunk chunk in ecs._world.Query(in description))
            {
                _chunks.Add(chunk);
            }

            _index = -1;
            _isWalking = true;
            ecs._openLoops++;
        }
    }
}
