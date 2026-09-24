using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Magic.Attributes;
using Magic.Interfaces;
using Magic.Services;
using System.Runtime.InteropServices;
using FlecsEntity = Flecs.NET.Core.Entity;
using FlecsWorldHandle = Flecs.NET.Core.World;
using Handle = Magic.Contexts.Handle;

namespace FlecsGem;

/// <summary>
/// The pile of entities, stored in a flecs world. Entity handles are flecs ids, so converting between the two
/// costs nothing. Components live in flecs' native columns; nothing about an entity is a managed object.
/// </summary>
[Gem(name: "Flecs ECS", version: "1.0.0", description: "Entity component system built on flecs.", author: "Konfus", isStatic: true)]
[GemExport(typeof(IWorld))]
internal sealed unsafe class FlecsWorld : IWorld, IDisposable
{
    private readonly FlecsEntity _singletons;
    private readonly IDisposable[] _registrations;

    public FlecsWorld(SystemScheduler systems)
    {
        Native = FlecsWorldHandle.Create();
        Native.SetThreads(Math.Max(1, Environment.ProcessorCount - 1));
        // Never Progress() the world: the main loop calls SystemScheduler once per phase; that call runs the
        // matching flecs pipeline, which runs every enabled system in that phase, sharding Parallel() ones
        // across the worker threads.
        Phases = new Phases(Native);
        _singletons = Native.Entity("Singletons");

        // SystemScheduler hands out milliseconds; systems get seconds.
        _registrations =
        [
            systems.Schedule(ms => Process(UpdateType.Update, (float)(ms / 1000d)), UpdateType.Update),
            systems.Schedule(ms => Process(UpdateType.FixedUpdate, (float)(ms / 1000d)), UpdateType.FixedUpdate),
            systems.Schedule(ms => Process(UpdateType.LateUpdate, (float)(ms / 1000d)), UpdateType.LateUpdate),
        ];
    }

    /// <summary>Phase tags and pipelines, one per <see cref="UpdateType"/>. Used by the scheduler.</summary>
    internal Phases Phases { get; }

    internal FlecsWorldHandle Native { get; }

    /// <summary>True once the flecs world is gone; handles created from it become no-ops.</summary>
    internal bool IsDisposed { get; private set; }

    public int EntityCount => Native.Count(Ecs.Any);


    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        foreach (IDisposable registration in _registrations)
            registration.Dispose();
        Native.Dispose(); // destroys every entity, query and system with it
    }

    private void Process(UpdateType phase, float dt)
    {
        if (IsDisposed)
            return;
        Native.RunPipeline(Phases.Pipeline(phase), dt);
    }

    public Handle Create(string? name = null, Handle parent = default)
    {
        // Anonymous first, then parent, then name: naming an entity that already has a parent scopes the
        // name to its siblings, which is what "unique among siblings" means.
        FlecsEntity entity = Native.Entity();
        if (parent.IsValid)
            entity.ChildOf(parent.Id);
        if (name is not null)
            entity.SetName(name);
        return new Handle(entity.Id);
    }

    public void Create(Span<Handle> ids)
    {
        if (ids.IsEmpty)
            return;

        // One table insert for the whole batch; the ids come back in a buffer flecs owns until the next bulk call.
        flecs.ecs_bulk_desc_t desc = default;
        desc.count = ids.Length;
        ulong* created = flecs.ecs_bulk_init(Native.Handle, &desc);
        new ReadOnlySpan<ulong>(created, ids.Length).CopyTo(MemoryMarshal.Cast<Handle, ulong>(ids));
    }

    public void Destroy(Handle entity)
    {
        Handle(entity).Destruct();
    }

    public bool IsAlive(Handle entity)
    {
        return entity.IsValid && Native.IsAlive(entity.Id);
    }

    public void Enable(Handle entity, bool enabled = true)
    {
        FlecsEntity native = Handle(entity);
        if (enabled)
            native.Enable();
        else
            native.Disable();
    }

    public bool IsEnabled(Handle entity)
    {
        return Handle(entity).Enabled();
    }

    public string? GetName(Handle entity)
    {
        string name = Handle(entity).Name();
        return name.Length == 0 ? null : name;
    }

    public void SetName(Handle entity, string? name)
    {
        if (name is null)
            flecs.ecs_set_name(Native.Handle, entity.Id, null);
        else
            Handle(entity).SetName(name);
    }

    public Handle Lookup(string path)
    {
        return new Handle(Native.Lookup(path).Id);
    }

    public Handle GetParent(Handle entity)
    {
        return new Handle(Handle(entity).Parent().Id);
    }

    public void SetParent(Handle entity, Handle parent)
    {
        FlecsEntity native = Handle(entity);
        if (parent.IsValid)
            native.ChildOf(parent.Id);
        else
            native.Remove(Ecs.ChildOf, Ecs.Wildcard);
    }

    public Handle[] GetChildren(Handle parent)
    {
        List<Handle> children = [];
        Handle(parent).Children((FlecsEntity child) => children.Add(new Handle(child.Id)));
        return [.. children];
    }

    public void Set<T>(Handle entity, in T value) where T : unmanaged
    {
        Handle(entity).Set(value);
    }

    public ref T Get<T>(Handle entity) where T : unmanaged
    {
        FlecsEntity native = Handle(entity);
        if (!native.Has<T>())
            throw new InvalidOperationException($"{entity} has no {typeof(T).Name} component.");
        return ref native.GetMut<T>();
    }

    public bool TryGet<T>(Handle entity, out T value) where T : unmanaged
    {
        FlecsEntity native = Handle(entity);
        if (native.Has<T>())
        {
            value = native.Get<T>();
            return true;
        }
        value = default;
        return false;
    }

    public bool Has<T>(Handle entity) where T : unmanaged
    {
        return Handle(entity).Has<T>();
    }

    public void Add<T>(Handle entity) where T : unmanaged
    {
        Handle(entity).Add<T>();
    }

    public void Remove<T>(Handle entity) where T : unmanaged
    {
        Handle(entity).Remove<T>();
    }

    public Handle Singleton()
    {
        return new Handle(_singletons.Id);
    }

    public ref T Singleton<T>() where T : unmanaged
    {
        return ref _singletons.Ensure<T>();
    }

    public IWorldQueryBuilder<T1> Query<T1>() where T1 : unmanaged
    {
        return new FlecsQueryBuilder<T1>(this);
    }

    public IWorldQueryBuilder<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged
    {
        return new FlecsQueryBuilder<T1, T2>(this);
    }

    public IWorldQueryBuilder<T1, T2, T3> Query<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        return new FlecsQueryBuilder<T1, T2, T3>(this);
    }

    public IWorldQueryBuilder<T1, T2, T3, T4> Query<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        return new FlecsQueryBuilder<T1, T2, T3, T4>(this);
    }

    public IWorldPipelineBuilder Schedule(string name)
    {
        return new FlecsScheduleBuilder(this, name);
    }

    private FlecsEntity Handle(Handle entity)
    {
        return Native.Entity(entity.Id);
    }
}

/// <summary>
/// One flecs phase tag and one pipeline per <see cref="UpdateType"/>. A pipeline is a query over systems; running
/// it runs every enabled system tagged with its phase, in scheduling order.
/// </summary>
internal sealed class Phases
{
    private readonly ulong[] _tags = new ulong[3];
    private readonly ulong[] _pipelines = new ulong[3];

    public Phases(FlecsWorldHandle world)
    {
        foreach (UpdateType phase in Enum.GetValues<UpdateType>())
        {
            FlecsEntity tag = world.Entity($"Magic.{phase}").Add(Ecs.Phase);
            _tags[(int)phase] = tag.Id;
            _pipelines[(int)phase] = world.Pipeline($"Magic.{phase}Pipeline")
                .With(Ecs.System)
                .With(tag.Id)
                .Build().Entity.Id;
        }
    }

    public ulong Tag(UpdateType phase)
    {
        return _tags[(int)phase];
    }

    public ulong Pipeline(UpdateType phase)
    {
        return _pipelines[(int)phase];
    }
}
