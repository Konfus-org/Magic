using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Arch.Core;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Interfaces;
using Magic.Services;
using Schedulers;
using ArchEntity = Arch.Core.Entity;
using ArchWorld = Arch.Core.World;
using Entity = Magic.Contexts.ECS.Entity;

namespace Magic.ArchEcs;

/// <summary>
/// The entity world, in one Arch world, on the main thread. Entities are Arch's, bit for bit, and components live in
/// Arch's chunks, so a query hands out spans straight over them and allocates nothing. Systems run in this gem's hook
/// for their phase, in the order they were added; a parallel system's chunks are spread over Arch's own job threads.
/// <para>
/// Arch trusts its caller, so this gem checks what Arch would let go wrong silently, and throws instead: a call from a
/// thread other than the main one, a structural change during a query's loop or a parallel system (Arch leaves their
/// spans stale), any call on an entity that is not alive (Arch applies it to whatever entity has the slot now) and a
/// <see cref="Get{T}"/> of a component the entity lacks (Arch hands back a reference to nothing).
/// </para>
/// </summary>
internal sealed class ArchEcsGem : IGem, IEcs, IDisposable
{
    private readonly ArchWorld _world;
    private readonly Threads _threads;

    /// <summary>
    /// The systems, by phase: Update, FixedUpdate and LateUpdate. A phase's array is replaced rather than changed, so a
    /// phase runs over the systems it started with, whatever they add or take off.
    /// </summary>
    private readonly Scheduled[][] _systems = [[], [], []];

    /// <summary>
    /// Each query's walk over its chunks, by the type of chunk it hands out: made the first time the query is asked for,
    /// and reused.
    /// </summary>
    private readonly Dictionary<Type, object> _walks = [];

    /// <summary>
    /// The ECS's type of each component an untyped create has named, found through its typed registration, so a struct
    /// is measured as the typed calls measure it. Main thread.
    /// </summary>
    private readonly Dictionary<Type, ComponentType> _componentTypes = [];

    /// <summary>
    /// How many query loops and parallel systems are running. A structural change now would move entities out from
    /// under their spans.
    /// </summary>
    private int _querying;

    /// <summary>
    /// A parallel system is running, so it may read the world from Arch's job threads.
    /// </summary>
    private volatile bool _isParallel;

    /// <summary>
    /// What a parallel system threw first, thrown on the main thread once every chunk is done.
    /// </summary>
    private Exception? _thrown;

    /// <summary>
    /// Arch's job threads, which run parallel systems; made on the main thread's first frame, since Arch schedules
    /// only from the thread that made them, and so before any system runs. Written there before the render thread
    /// disposes the gem.
    /// </summary>
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Dispose hands it to the main thread, the only one it can be disposed on.")]
    private JobScheduler? _jobs;

    /// <summary>
    /// Where a batch's entities are made, grown as needed and reused.
    /// </summary>
    private ArchEntity[] _created = [];

    public ArchEcsGem(Threads threads)
    {
        // An entity becomes Arch's and back by reinterpreting its bits, so an Arch that laid its own out differently
        // would scramble every handle.
        ArchEntity probe = Unsafe.BitCast<Entity, ArchEntity>(new Entity(1, 2, 3));
        if (probe.Id != 1 || probe.WorldId != 2 || probe.Version != 3)
        {
            throw new InvalidOperationException($"Arch lays out its entity as {probe}, not as Magic's (Id, EcsWorld, Version).");
        }

        _threads = threads;
        _world = ArchWorld.Create();
        threads.Post(EngineThread.Main, StartJobs);
    }

    public void Dispose()
    {
        _world.Dispose();

        // Arch's job threads end only from the thread that made them, which runs this before it ends. Until they do, they
        // keep the process alive.
        if (_jobs is { } jobs)
        {
            _threads.Post(EngineThread.Main, jobs.Dispose);
        }
    }

    public void Update(in Frame frame) => Run(Phase.Update, frame);

    public void FixedUpdate(in Frame frame) => Run(Phase.FixedUpdate, frame);

    public void LateUpdate(in Frame frame) => Run(Phase.LateUpdate, frame);

    public Entity Create()
    {
        ThrowIfNotMain();
        ThrowIfQuerying();
        return Unsafe.BitCast<ArchEntity, Entity>(_world.Create());
    }

    public Entity Create(ReadOnlySpan<IComponent> components)
    {
        ThrowIfNotMain();
        ThrowIfQuerying();
        ComponentType[] types = new ComponentType[components.Length];
        object[] values = new object[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            types[i] = ComponentTypeOf(components[i].GetType());
            values[i] = components[i];
        }

        ArchEntity created = _world.Create(types);
        _world.SetRange(created, values);
        return Unsafe.BitCast<ArchEntity, Entity>(created);
    }

    public ReadOnlySpan<Entity> Create<T1>(ReadOnlySpan<T1> first)
        where T1 : unmanaged, IComponent
    {
        Span<ArchEntity> created = CreateBatch(first.Length, All<T1>.Signature);
        for (int i = 0; i < created.Length; i++)
        {
            _world.Set(created[i], in first[i]);
        }

        return MemoryMarshal.Cast<ArchEntity, Entity>(created);
    }

    public ReadOnlySpan<Entity> Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        if (second.Length != first.Length)
        {
            throw new ArgumentException($"A batch's spans are the same length: {first.Length} {typeof(T1).Name}, {second.Length} {typeof(T2).Name}.", nameof(second));
        }

        Span<ArchEntity> created = CreateBatch(first.Length, All<T1, T2>.Signature);
        for (int i = 0; i < created.Length; i++)
        {
            _world.Set(created[i], in first[i]);
            _world.Set(created[i], in second[i]);
        }

        return MemoryMarshal.Cast<ArchEntity, Entity>(created);
    }

    public void Destroy(Entity entity)
    {
        ThrowIfNotMain();
        ArchEntity alive = Alive(entity);
        ThrowIfQuerying();
        _world.Destroy(alive);
    }

    public bool IsAlive(Entity entity)
    {
        ThrowIfCannotRead();
        return _world.IsAlive(ToArch(entity));
    }

    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent
    {
        ThrowIfNotMain();
        ArchEntity alive = Alive(entity);
        ref T existing = ref _world.TryGetRef<T>(alive, out bool has);
        if (has)
        {
            // Not structural, so it is fine inside a query.
            existing = component;
            return;
        }

        ThrowIfQuerying();
        _world.Add(alive, component);
    }

    public ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfCannotRead();
        ref T component = ref _world.TryGetRef<T>(Alive(entity), out bool has);
        if (!has)
        {
            throw new InvalidOperationException($"{entity} has no {typeof(T).Name}.");
        }

        return ref component;
    }

    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent
        => IsAlive(entity) && _world.Has<T>(ToArch(entity));

    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfNotMain();
        ArchEntity alive = Alive(entity);
        if (!_world.Has<T>(alive))
        {
            return;
        }

        ThrowIfQuerying();
        _world.Remove<T>(alive);
    }

    public EcsQuery Query() => new(WalkOf(All.Description, All.Cut));

    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent
        => new(WalkOf(All<T1>.Description, All<T1>.Cut));

    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        => new(WalkOf(All<T1, T2>.Description, All<T1, T2>.Cut));

    public EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        => new(WalkOf(All<T1, T2, T3>.Description, All<T1, T2, T3>.Cut));

    public EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        => new(WalkOf(All<T1, T2, T3, T4>.Description, All<T1, T2, T3, T4>.Cut));

    public Subscription Add(ISystem system) => Schedule(system, system.Phase, frame => system.Run(frame, Query()));

    public Subscription Add<T1>(ISystem<T1> system)
        where T1 : unmanaged, IComponent
        => Schedule(system, system.Phase, frame => system.Run(frame, Query<T1>()));

    public Subscription Add<T1, T2>(ISystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        => Schedule(system, system.Phase, frame => system.Run(frame, Query<T1, T2>()));

    public Subscription Add<T1, T2, T3>(ISystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        => Schedule(system, system.Phase, frame => system.Run(frame, Query<T1, T2, T3>()));

    public Subscription Add<T1, T2, T3, T4>(ISystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        => Schedule(system, system.Phase, frame => system.Run(frame, Query<T1, T2, T3, T4>()));

    public Subscription Add(IParallelSystem system)
        => AddParallel(system, system.Phase, All.Description, (in Frame frame, in Chunk chunk) => system.Run(frame, All.Cut(chunk)));

    public Subscription Add<T1>(IParallelSystem<T1> system)
        where T1 : unmanaged, IComponent
        => AddParallel(system, system.Phase, All<T1>.Description, (in Frame frame, in Chunk chunk) => system.Run(frame, All<T1>.Cut(chunk)));

    public Subscription Add<T1, T2>(IParallelSystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        => AddParallel(system, system.Phase, All<T1, T2>.Description, (in Frame frame, in Chunk chunk) => system.Run(frame, All<T1, T2>.Cut(chunk)));

    public Subscription Add<T1, T2, T3>(IParallelSystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        => AddParallel(system, system.Phase, All<T1, T2, T3>.Description, (in Frame frame, in Chunk chunk) => system.Run(frame, All<T1, T2, T3>.Cut(chunk)));

    public Subscription Add<T1, T2, T3, T4>(IParallelSystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        => AddParallel(system, system.Phase, All<T1, T2, T3, T4>.Description, (in Frame frame, in Chunk chunk) => system.Run(frame, All<T1, T2, T3, T4>.Cut(chunk)));

    private static ArchEntity ToArch(Entity entity) => Unsafe.BitCast<Entity, ArchEntity>(entity);

    /// <summary>
    /// A chunk's live entities as Magic's: Arch's arrays run to the chunk's capacity, past its last entity.
    /// </summary>
    private static ReadOnlySpan<Entity> Entities(Chunk chunk) => MemoryMarshal.Cast<ArchEntity, Entity>(chunk.Entities.AsSpan(0, chunk.Count));

    /// <summary>
    /// A chunk's <typeparamref name="T"/> components, at its live entities' indexes.
    /// </summary>
    private static Span<T> Components<T>(Chunk chunk) => chunk.GetArray<T>().AsSpan(0, chunk.Count);

    /// <summary>
    /// Runs the phase's systems. One that throws is logged and taken off, and the phase goes on without it.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever a system throws, it is logged and the system is taken off; the others go on.")]
    private void Run(Phase phase, in Frame frame)
    {
        foreach (Scheduled scheduled in _systems[(int)phase])
        {
            try
            {
                scheduled.Run(frame);
            }
            catch (Exception ex)
            {
                Debugging.Error($"System {scheduled.System.GetType().Name} threw in {phase} and was taken off: {ex}");
                TakeOff((int)phase, scheduled);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="run"/> every frame in <paramref name="phase"/>, as <paramref name="system"/>, after the
    /// systems added before it, until the subscription is disposed.
    /// </summary>
    private Subscription Schedule(object system, Phase phase, Action<Frame> run)
    {
        ThrowIfNotMain();
        if (phase > Phase.LateUpdate)
        {
            throw new ArgumentException($"System {system.GetType().Name} runs in {phase}, the render thread's, while the main thread changes the world: a system runs in Update, FixedUpdate or LateUpdate.", nameof(system));
        }

        int index = (int)phase;
        Scheduled scheduled = new(system, run);
        _systems[index] = [.. _systems[index], scheduled];
        return new Subscription(() => TakeOff(index, scheduled));
    }

    /// <summary>
    /// Schedules a parallel system: every frame, <paramref name="run"/> on each chunk <paramref name="description"/>
    /// matches, on Arch's job threads.
    /// </summary>
    private Subscription AddParallel(object system, Phase phase, QueryDescription description, ChunkRun run)
        => Schedule(system, phase, frame => RunParallel(description, new ChunkJob(this, frame, run)));

    private void TakeOff(int phase, Scheduled scheduled)
    {
        Scheduled[] systems = _systems[phase];
        int index = Array.IndexOf(systems, scheduled);
        if (index < 0)
        {
            return;
        }

        _systems[phase] = [.. systems.AsSpan(0, index), .. systems.AsSpan(index + 1)];
    }

    /// <summary>
    /// The walk over the chunks <paramref name="description"/> matches, each cut down by <paramref name="cut"/>; made
    /// the first time it is asked for.
    /// </summary>
    private IEnumerable<TChunk> WalkOf<TChunk>(in QueryDescription description, Func<Chunk, TChunk> cut)
        where TChunk : allows ref struct
    {
        ThrowIfNotMain();
        ref object? walk = ref CollectionsMarshal.GetValueRefOrAddDefault(_walks, typeof(TChunk), out _);
        walk ??= new Walk<TChunk>(this, description, cut);
        return (Walk<TChunk>)walk;
    }

    /// <summary>
    /// Arch's job threads, made on the main thread, which is the only one Arch will schedule from. The main and render
    /// threads are busy every frame, so the jobs take the other cores.
    /// </summary>
    private void StartJobs()
    {
        _jobs = new JobScheduler(new JobScheduler.Config
        {
            ThreadPrefixName = "Arch job ",
            ThreadCount = Math.Max(1, Environment.ProcessorCount - 2),
        });
        ArchWorld.SharedJobScheduler = _jobs;
    }

    /// <summary>
    /// The ECS's type of <paramref name="type"/>, a component known only as the program runs, read from its typed
    /// registration the first time.
    /// </summary>
    private ComponentType ComponentTypeOf(Type type)
    {
        if (_componentTypes.TryGetValue(type, out ComponentType known))
        {
            return known;
        }

        ComponentType registered = typeof(Component<>).MakeGenericType(type).GetField(nameof(Component<int>.ComponentType))?.GetValue(null) is ComponentType found
            ? found
            : throw new InvalidOperationException($"The ECS has no type for {type.Name}.");
        _componentTypes[type] = registered;
        return registered;
    }

    /// <summary>
    /// <paramref name="count"/> new entities of <paramref name="signature"/>, in the reused batch buffer.
    /// </summary>
    private Span<ArchEntity> CreateBatch(int count, in Signature signature)
    {
        ThrowIfNotMain();
        ThrowIfQuerying();
        if (_created.Length < count)
        {
            _created = new ArchEntity[BitOperations.RoundUpToPowerOf2((uint)count)];
        }

        Span<ArchEntity> created = _created.AsSpan(0, count);
        if (count > 0)
        {
            _world.Create(created, in signature, count);
        }

        return created;
    }

    /// <summary>
    /// Runs <paramref name="job"/> on every chunk <paramref name="description"/> matches, on Arch's job threads, and
    /// throws what a chunk threw once they are all done.
    /// </summary>
    private void RunParallel(in QueryDescription description, in ChunkJob job)
    {
        _querying++;
        _isParallel = true;
        try
        {
            _world.InlineParallelChunkQuery(in description, in job);
        }
        finally
        {
            _isParallel = false;
            _querying--;
        }

        if (Interlocked.Exchange(ref _thrown, null) is { } thrown)
        {
            ExceptionDispatchInfo.Throw(thrown);
        }
    }

    /// <summary>
    /// Arch's handle for <paramref name="entity"/>, which must be alive.
    /// </summary>
    private ArchEntity Alive(Entity entity)
    {
        ArchEntity arch = ToArch(entity);
        if (!_world.IsAlive(arch))
        {
            throw new InvalidOperationException($"{entity} is not alive.");
        }

        return arch;
    }

    private void ThrowIfNotMain()
    {
        if (!_threads.IsCurrent(EngineThread.Main))
        {
            throw new InvalidOperationException("The world is the main thread's: hand the work to it (Threads.Post or InvokeAsync).");
        }
    }

    /// <summary>
    /// Reading is the main thread's too, but for a parallel system, on whichever thread it runs.
    /// </summary>
    private void ThrowIfCannotRead()
    {
        if (!_isParallel)
        {
            ThrowIfNotMain();
        }
    }

    private void ThrowIfQuerying()
    {
        if (_querying > 0)
        {
            throw new InvalidOperationException("An entity's components cannot change during a query's loop or a parallel system: collect what to change, and change it after.");
        }
    }

    /// <summary>
    /// A parallel system's run over one Arch chunk, which it cuts down to the components the system looks at.
    /// </summary>
    private delegate void ChunkRun(in Frame frame, in Chunk chunk);

    /// <summary>
    /// A system added to a phase: the system, which names it in the log and takes it off, and how to run it.
    /// </summary>
    private readonly record struct Scheduled(object System, Action<Frame> Run);

    /// <summary>
    /// The query over every entity, described once, and how a chunk is cut down to its entities.
    /// </summary>
    private static class All
    {
        public static readonly QueryDescription Description = new();

        public static EcsChunk Cut(Chunk chunk) => new(Entities(chunk));
    }

    /// <summary>
    /// The query over the entities with a <typeparamref name="T1"/> and the signature of an entity with just that,
    /// described once, and how a chunk is cut down to those components.
    /// </summary>
    private static class All<T1>
        where T1 : unmanaged, IComponent
    {
        public static readonly QueryDescription Description = new QueryDescription().WithAll<T1>();

        public static readonly Signature Signature = new(Component<T1>.ComponentType);

        public static EcsChunk<T1> Cut(Chunk chunk) => new(Entities(chunk), Components<T1>(chunk));
    }

    /// <summary>
    /// <see cref="All{T1}"/> with both components.
    /// </summary>
    private static class All<T1, T2>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        public static readonly QueryDescription Description = new QueryDescription().WithAll<T1, T2>();

        public static readonly Signature Signature = new(Component<T1>.ComponentType, Component<T2>.ComponentType);

        public static EcsChunk<T1, T2> Cut(Chunk chunk) => new(Entities(chunk), Components<T1>(chunk), Components<T2>(chunk));
    }

    /// <summary>
    /// The query over the entities with all three components, described once, and how a chunk is cut down to them.
    /// </summary>
    private static class All<T1, T2, T3>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        public static readonly QueryDescription Description = new QueryDescription().WithAll<T1, T2, T3>();

        public static EcsChunk<T1, T2, T3> Cut(Chunk chunk) => new(Entities(chunk), Components<T1>(chunk), Components<T2>(chunk), Components<T3>(chunk));
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
        public static readonly QueryDescription Description = new QueryDescription().WithAll<T1, T2, T3, T4>();

        public static EcsChunk<T1, T2, T3, T4> Cut(Chunk chunk) => new(Entities(chunk), Components<T1>(chunk), Components<T2>(chunk), Components<T3>(chunk), Components<T4>(chunk));
    }

    /// <summary>
    /// A query's walk over its chunks, which it gathers as a loop starts: nothing can move an entity until the loop
    /// ends, so they stay current. One per query and reused, so a loop allocates nothing; a loop over the same query
    /// started inside one gets a walk of its own.
    /// </summary>
    private sealed class Walk<TChunk>(ArchEcsGem ecs, QueryDescription description, Func<Chunk, TChunk> cut)
        : IEnumerable<TChunk>, IEnumerator<TChunk>
        where TChunk : allows ref struct
    {
        private readonly List<Chunk> _chunks = [];
        private int _index;
        private bool _isWalking;

        public void Dispose()
        {
            if (!_isWalking)
            {
                return;
            }

            _isWalking = false;
            ecs._querying--;
        }

        public TChunk Current => cut(_chunks[_index]);

        object IEnumerator.Current => throw new NotSupportedException("A chunk is spans over storage, which cannot be boxed.");

        public IEnumerator<TChunk> GetEnumerator()
        {
            Walk<TChunk> walk = _isWalking ? new Walk<TChunk>(ecs, description, cut) : this;
            walk.Start();
            return walk;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool MoveNext() => ++_index < _chunks.Count;

        public void Reset() => throw new NotSupportedException("A walk runs once per loop: start another loop to walk again.");

        private void Start()
        {
            _chunks.Clear();
            foreach (ref Chunk chunk in ecs._world.Query(in description))
            {
                _chunks.Add(chunk);
            }

            _index = -1;
            _isWalking = true;
            ecs._querying++;
        }
    }

    /// <summary>
    /// A parallel system's run over one chunk, on one of Arch's job threads. What it throws is kept for the main thread,
    /// rather than ending the job thread and the process with it.
    /// </summary>
    private readonly struct ChunkJob(ArchEcsGem ecs, Frame frame, ChunkRun run) : IChunkJob
    {
        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "What a chunk throws is thrown on the main thread once the system is done.")]
        public void Execute(ref Chunk chunk)
        {
            try
            {
                run(in frame, in chunk);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref ecs._thrown, ex, null);
            }
        }
    }
}
