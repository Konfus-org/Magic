using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Arch.Core;
using Magic.Attributes;
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
/// for their phase, in the order they were added, and while the world is paused only the unpausable ones; a parallel
/// system's chunks are spread over Arch's own job threads.
/// Arch trusts its caller, so this gem checks what Arch would let go wrong silently, and throws instead; the README's
/// Guards table says which.
/// </summary>
internal sealed class ArchEcsGem : IGem, IEcs, IDisposable
{
    private static readonly Meter _meter = new("Magic.Gems.ArchEcs");
    private static readonly Gauge<int> _entities = _meter.CreateGauge<int>(
        "magic.gems.arch_ecs.entities",
        "{entity}",
        "Entities alive.");
    private static readonly Gauge<int> _archetypes = _meter.CreateGauge<int>(
        "magic.gems.arch_ecs.archetypes",
        "{archetype}",
        "Archetypes, one per set of component types entities have.");

    private readonly ArchWorld _world;
    private readonly Threads _threads;

    /// <summary>
    /// The systems, by phase: Update, FixedUpdate and LateUpdate. A phase's array is replaced rather than changed, so a
    /// phase runs over the systems it started with, whatever they add or take off.
    /// </summary>
    private readonly Scheduled[][] _systems = [[], [], []];

    /// <summary>
    /// Each query's walk over its chunks, by the type of chunk it hands out: made the first time the query is asked
    /// for, and reused.
    /// </summary>
    private readonly Dictionary<Type, object> _walks = [];

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
    /// Arch's job threads, which run parallel systems; made as the main thread's first Update starts, since Arch
    /// schedules only from the thread that made them, and so before any system runs, and ended in Stop, on that thread.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "Stop disposes it on the main thread, the only one it can be disposed on.")]
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
            throw new InvalidOperationException(
                $"Arch lays out its entity as {probe}, not as Magic's (Id, EcsWorld, Version).");
        }

        _threads = threads;
        _world = ArchWorld.Create();
    }

    public void Dispose() => _world.Dispose();

    public void Update(in Frame frame)
    {
        if (_jobs is null) StartJobs();

        _entities.Record(_world.Size);
        _archetypes.Record(_world.Archetypes.Count);
        Run(Phase.Update, frame);
    }

    public void FixedUpdate(in Frame frame) => Run(Phase.FixedUpdate, frame);

    public void LateUpdate(in Frame frame) => Run(Phase.LateUpdate, frame);

    /// <summary>
    /// Ends Arch's job threads, which end only from the thread that made them, the main one; they would otherwise keep
    /// the process alive.
    /// </summary>
    public void Stop()
    {
        _jobs?.Dispose();
        _jobs = null;
    }

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
            types[i] = GetComponentType(components[i].GetType());
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
            throw new ArgumentException(
                $"A batch's spans are the same length: {first.Length} {typeof(T1).Name}, "
                    + $"{second.Length} {typeof(T2).Name}.",
                nameof(second));
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
        ArchEntity alive = GetAlive(entity);
        ThrowIfQuerying();
        _world.Destroy(alive);
    }

    public bool IsAlive(Entity entity)
    {
        ThrowIfCannotRead();

        return _world.IsAlive(Unsafe.BitCast<Entity, ArchEntity>(entity));
    }

    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent
    {
        ThrowIfNotMain();
        ArchEntity alive = GetAlive(entity);
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

    public void Set(Entity entity, ReadOnlySpan<IComponent> components)
    {
        ThrowIfNotMain();
        ArchEntity alive = GetAlive(entity);

        // Every component is set, but only the types the entity lacks are added, all in one move.
        ComponentType[] types = new ComponentType[components.Length];
        object[] values = new object[components.Length];
        int added = 0;
        for (int i = 0; i < components.Length; i++)
        {
            values[i] = components[i];
            ComponentType type = GetComponentType(components[i].GetType());
            if (!_world.Has(alive, type)) types[added++] = type;
        }

        if (added > 0)
        {
            ThrowIfQuerying();
            _world.AddRange(alive, types.AsSpan(0, added));
        }

        _world.SetRange(alive, values);
    }

    public IComponent[] GetComponents(Entity entity)
    {
        ThrowIfNotMain();
        object?[] held = _world.GetAllComponents(GetAlive(entity));
        IComponent[] components = new IComponent[held.Length];
        for (int i = 0; i < held.Length; i++)
        {
            components[i] = held[i] as IComponent ?? throw new InvalidOperationException(
                $"{entity} holds a {held[i]?.GetType().Name ?? "null"}, which is no component.");
        }

        return components;
    }

    public ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfCannotRead();
        ref T component = ref _world.TryGetRef<T>(GetAlive(entity), out bool has);

        if (!has) throw new InvalidOperationException($"{entity} has no {typeof(T).Name}.");

        return ref component;
    }

    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent
        => IsAlive(entity) && _world.Has<T>(Unsafe.BitCast<Entity, ArchEntity>(entity));

    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfNotMain();
        ArchEntity alive = GetAlive(entity);

        if (!_world.Has<T>(alive)) return;

        ThrowIfQuerying();
        _world.Remove<T>(alive);
    }

    public EcsQuery Query() => new(GetWalk(All.Description, All.Cut));

    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent
        => new(GetWalk(All<T1>.Description, All<T1>.Cut));

    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        => new(GetWalk(All<T1, T2>.Description, All<T1, T2>.Cut));

    public EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        => new(GetWalk(All<T1, T2, T3>.Description, All<T1, T2, T3>.Cut));

    public EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        => new(GetWalk(All<T1, T2, T3, T4>.Description, All<T1, T2, T3, T4>.Cut));

    public Subscription Register(ISystem system) => Schedule(system, system.Phase, frame => system.Run(frame, Query()));

    public Subscription Register<T1>(ISystem<T1> system)
        where T1 : unmanaged, IComponent
        => Schedule(system, system.Phase, frame => system.Run(frame, Query<T1>()));

    public Subscription Register<T1, T2>(ISystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        => Schedule(system, system.Phase, frame => system.Run(frame, Query<T1, T2>()));

    public Subscription Register<T1, T2, T3>(ISystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        => Schedule(system, system.Phase, frame => system.Run(frame, Query<T1, T2, T3>()));

    public Subscription Register<T1, T2, T3, T4>(ISystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        => Schedule(system, system.Phase, frame => system.Run(frame, Query<T1, T2, T3, T4>()));

    public Subscription Register(IParallelSystem system)
        => ScheduleParallel(
            system,
            system.Phase,
            All.Description,
            (in Frame frame, in Chunk chunk) => system.Run(frame, All.Cut(chunk)));

    public Subscription Register<T1>(IParallelSystem<T1> system)
        where T1 : unmanaged, IComponent
        => ScheduleParallel(
            system,
            system.Phase,
            All<T1>.Description,
            (in Frame frame, in Chunk chunk) => system.Run(frame, All<T1>.Cut(chunk)));

    public Subscription Register<T1, T2>(IParallelSystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        => ScheduleParallel(
            system,
            system.Phase,
            All<T1, T2>.Description,
            (in Frame frame, in Chunk chunk) => system.Run(frame, All<T1, T2>.Cut(chunk)));

    public Subscription Register<T1, T2, T3>(IParallelSystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        => ScheduleParallel(
            system,
            system.Phase,
            All<T1, T2, T3>.Description,
            (in Frame frame, in Chunk chunk) => system.Run(frame, All<T1, T2, T3>.Cut(chunk)));

    public Subscription Register<T1, T2, T3, T4>(IParallelSystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
        => ScheduleParallel(
            system,
            system.Phase,
            All<T1, T2, T3, T4>.Description,
            (in Frame frame, in Chunk chunk) => system.Run(frame, All<T1, T2, T3, T4>.Cut(chunk)));

    /// <summary>
    /// A chunk's live entities as Magic's: Arch's arrays run to the chunk's capacity, past its last entity.
    /// </summary>
    private static ReadOnlySpan<Entity> GetEntities(Chunk chunk)
        => MemoryMarshal.Cast<ArchEntity, Entity>(chunk.Entities.AsSpan(0, chunk.Count));

    /// <summary>
    /// A chunk's <typeparamref name="T"/> components, at its live entities' indexes.
    /// </summary>
    private static Span<T> GetComponents<T>(Chunk chunk) => chunk.GetArray<T>().AsSpan(0, chunk.Count);

    /// <summary>
    /// The ECS's type of <paramref name="type"/>, a component known only as the program runs, from Arch's registry, or
    /// from its typed registration the first time, so a struct is measured as the typed calls measure it.
    /// </summary>
    private static ComponentType GetComponentType(Type type)
    {
        if (ComponentRegistry.TryGet(type, out ComponentType registered)) return registered;

        return typeof(Component<>).MakeGenericType(type).GetField(nameof(Component<int>.ComponentType))?
            .GetValue(null) is ComponentType found
            ? found
            : throw new InvalidOperationException($"The ECS has no type for {type.Name}.");
    }

    /// <summary>
    /// Runs the phase's systems, while the world is paused only those whose class is <see cref="UnpausableAttribute"/>.
    /// One that throws is logged and taken off, and the phase goes on without it.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a system throws, it is logged and the system is taken off; the others go on.")]
    private void Run(Phase phase, in Frame frame)
    {
        foreach (Scheduled scheduled in _systems[(int)phase])
        {
            if (frame.IsPaused && !scheduled.IsUnpausable) continue;

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
            throw new ArgumentException(
                $"System {system.GetType().Name} runs in {phase}; a system runs in Update, FixedUpdate or LateUpdate, "
                    + "the phases in which the main thread moves the world on.",
                nameof(system));
        }

        int index = (int)phase;
        Scheduled scheduled = new(system, run, system.GetType().IsDefined(typeof(UnpausableAttribute), inherit: false));
        _systems[index] = [.. _systems[index], scheduled];

        return new Subscription(() => TakeOff(index, scheduled));
    }

    /// <summary>
    /// Schedules a parallel system: every frame, <paramref name="run"/> on each chunk <paramref name="description"/>
    /// matches, on Arch's job threads.
    /// </summary>
    private Subscription ScheduleParallel(object system, Phase phase, QueryDescription description, ChunkRun run)
        => Schedule(system, phase, frame => RunParallel(description, new ChunkJob(this, frame, run)));

    private void TakeOff(int phase, Scheduled scheduled)
    {
        Scheduled[] systems = _systems[phase];
        int index = Array.IndexOf(systems, scheduled);

        if (index < 0) return;

        _systems[phase] = [.. systems.AsSpan(0, index), .. systems.AsSpan(index + 1)];
    }

    /// <summary>
    /// The walk over the chunks <paramref name="description"/> matches, each cut down by <paramref name="cut"/>; made
    /// the first time it is asked for.
    /// </summary>
    private IEnumerable<TChunk> GetWalk<TChunk>(in QueryDescription description, Func<Chunk, TChunk> cut)
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
    /// <paramref name="count"/> new entities of <paramref name="signature"/>, in the reused batch buffer.
    /// </summary>
    private Span<ArchEntity> CreateBatch(int count, in Signature signature)
    {
        ThrowIfNotMain();
        ThrowIfQuerying();
        if (_created.Length < count) _created = new ArchEntity[BitOperations.RoundUpToPowerOf2((uint)count)];

        Span<ArchEntity> created = _created.AsSpan(0, count);
        if (count > 0) _world.Create(created, in signature, count);

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

        if (Interlocked.Exchange(ref _thrown, null) is { } thrown) ExceptionDispatchInfo.Throw(thrown);
    }

    /// <summary>
    /// Arch's handle for <paramref name="entity"/>, which must be alive.
    /// </summary>
    private ArchEntity GetAlive(Entity entity)
    {
        ArchEntity arch = Unsafe.BitCast<Entity, ArchEntity>(entity);

        if (!_world.IsAlive(arch)) throw new InvalidOperationException($"{entity} is not alive.");

        return arch;
    }

    private void ThrowIfNotMain()
    {
        if (_threads.IsCurrent(EngineThread.Main)) return;

        throw new InvalidOperationException(
            "The world is the main thread's: hand the work to it (Threads.PostAsync).");
    }

    /// <summary>
    /// Reading is the main thread's too, but for a parallel system, on whichever thread it runs.
    /// </summary>
    private void ThrowIfCannotRead()
    {
        if (!_isParallel) ThrowIfNotMain();
    }

    private void ThrowIfQuerying()
    {
        if (_querying <= 0) return;

        throw new InvalidOperationException("An entity's components cannot change during a query's loop or a "
            + "parallel system: collect what to change, and change it after.");
    }

    /// <summary>
    /// A parallel system's run over one Arch chunk, which it cuts down to the components the system looks at.
    /// </summary>
    private delegate void ChunkRun(in Frame frame, in Chunk chunk);

    /// <summary>
    /// A system added to a phase: the system, which names it in the log and takes it off, how to run it, and whether
    /// it runs while the world is paused (<see cref="UnpausableAttribute"/>).
    /// </summary>
    private readonly record struct Scheduled(object System, Action<Frame> Run, bool IsUnpausable);

    /// <summary>
    /// A parallel system's run over one chunk, on one of Arch's job threads. What it throws is kept for the main
    /// thread, rather than ending the job thread and the process with it.
    /// </summary>
    private readonly struct ChunkJob(ArchEcsGem ecs, Frame frame, ChunkRun run) : IChunkJob
    {
        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "What a chunk throws is thrown on the main thread once the system is done.")]
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

        internal static EcsChunk<T1, T2> Cut(Chunk chunk)
            => new(GetEntities(chunk), GetComponents<T1>(chunk), GetComponents<T2>(chunk));
    }

    /// <summary>
    /// The query over the entities with all three components, described once, and how a chunk is cut down to them.
    /// </summary>
    private static class All<T1, T2, T3>
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        internal static readonly QueryDescription Description = new QueryDescription().WithAll<T1, T2, T3>();

        internal static EcsChunk<T1, T2, T3> Cut(Chunk chunk)
            => new(GetEntities(chunk), GetComponents<T1>(chunk), GetComponents<T2>(chunk), GetComponents<T3>(chunk));
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

        internal static EcsChunk<T1, T2, T3, T4> Cut(Chunk chunk) => new(
            GetEntities(chunk),
            GetComponents<T1>(chunk),
            GetComponents<T2>(chunk),
            GetComponents<T3>(chunk),
            GetComponents<T4>(chunk));
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

        public TChunk Current => cut(_chunks[_index]);

        object IEnumerator.Current
            => throw new NotSupportedException("A chunk is spans over storage, which cannot be boxed.");

        public void Dispose()
        {
            if (!_isWalking) return;

            _isWalking = false;
            ecs._querying--;
        }

        public IEnumerator<TChunk> GetEnumerator()
        {
            Walk<TChunk> walk = _isWalking ? new Walk<TChunk>(ecs, description, cut) : this;
            walk.Start();

            return walk;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool MoveNext() => ++_index < _chunks.Count;

        public void Reset()
            => throw new NotSupportedException("A walk runs once per loop: start another loop to walk again.");

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
}
