using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Magic;
using Magic.Contexts;
using Magic.Interfaces;
using Magic.Services;
using System.Runtime.CompilerServices;

namespace FlecsGem;

// A system is a flecs system entity: a query plus a callback, tagged with the phase it runs in. Its run callback
// is registered once and drives the iterator itself, so per frame the only indirection between flecs and the
// caller's code is the caller's own delegate. Parallel() lets flecs split the matched tables across its worker
// threads; each worker calls the run callback with its own share.

internal sealed unsafe class FlecsScheduleBuilder(FlecsWorld world, string name) : IWorldPipelineBuilder
{
    private UpdateType _phase = UpdateType.Update;

    public IWorldPipelineBuilder On(UpdateType phase)
    {
        _phase = phase;
        return this;
    }

    public IWorldPipelineBuilder<T1> Query<T1>() where T1 : unmanaged
        => new FlecsSystemBuilder<T1>(world, name, _phase);

    public IWorldPipelineBuilder<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged
        => new FlecsSystemBuilder<T1, T2>(world, name, _phase);

    public IWorldPipelineBuilder<T1, T2, T3> Query<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
        => new FlecsSystemBuilder<T1, T2, T3>(world, name, _phase);

    public IWorldPipelineBuilder<T1, T2, T3, T4> Query<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        => new FlecsSystemBuilder<T1, T2, T3, T4>(world, name, _phase);

    public IDisposable Run(Action<float> callback)
    {
        System_ system = new SystemBuilder(world.Native.Handle, name)
            .Kind(world.Phases.Tag(_phase))
            .Run((Iter it) => callback(it.DeltaTime()));
        return new FlecsSystem(world, system.Entity.Id);
    }
}

/// <summary>
/// A scheduled system. Disposing it destroys the flecs system entity. Whoever scheduled it keeps the handle and
/// disposes it when done (a gem in its own Dispose); otherwise the world would keep calling into an unloaded
/// assembly, and keep it alive.
/// </summary>
internal sealed class FlecsSystem : IDisposable
{
    private readonly FlecsWorld _world;
    private readonly ulong _id;

    public FlecsSystem(FlecsWorld world, ulong id)
    {
        _world = world;
        _id = id;
    }

    public void Dispose()
    {
        // Flecs.NET.Debug aborts on a dead entity rather than throwing, so check first.
        if (!_world.IsDisposed && _world.Native.IsAlive(_id))
            _world.Native.Entity(_id).Destruct();
    }
}

internal abstract unsafe class FlecsSystemBuilder(FlecsWorld world, string name, UpdateType phase)
{
    protected FlecsWorld World { get; } = world;
    protected SystemBuilder Builder = new SystemBuilder(world.Native.Handle, name).Kind(world.Phases.Tag(phase));

    protected IDisposable Schedule(Ecs.RunCallback run, Delegate callback)
    {
        return new FlecsSystem(World, Builder.Run(run).Entity.Id);
    }
}

internal sealed unsafe class FlecsSystemBuilder<T1> : FlecsSystemBuilder, IWorldPipelineBuilder<T1>
    where T1 : unmanaged
{
    public FlecsSystemBuilder(FlecsWorld world, string name, UpdateType phase) : base(world, name, phase) { Builder.With<T1>(); }

    public IWorldPipelineBuilder<T1> With<T>() where T : unmanaged { Builder.With<T>(); return this; }
    public IWorldPipelineBuilder<T1> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }
    public IWorldPipelineBuilder<T1> Parallel() { Builder.MultiThreaded(); return this; }

    public IDisposable Each(PipelineEachAction<T1> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();
        while (it.Next())
        {
            flecs.ecs_iter_t* p = it.Handle;
            ref T1 c1 = ref FlecsIter.First<T1>(p, 0);
            for (int i = 0, n = p->count; i < n; i++)
                action(dt, new Handle(p->entities[i]), ref Unsafe.Add(ref c1, i));
        }
    }, action);

    public IDisposable Run(PipelineChunkAction<T1> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();
        while (it.Next())
            action(dt, FlecsIter.Entities(it.Handle), FlecsIter.Column<T1>(it.Handle, 0));
    }, action);
}

internal sealed unsafe class FlecsSystemBuilder<T1, T2> : FlecsSystemBuilder, IWorldPipelineBuilder<T1, T2>
    where T1 : unmanaged where T2 : unmanaged
{
    public FlecsSystemBuilder(FlecsWorld world, string name, UpdateType phase) : base(world, name, phase) { Builder.With<T1>().With<T2>(); }

    public IWorldPipelineBuilder<T1, T2> With<T>() where T : unmanaged { Builder.With<T>(); return this; }
    public IWorldPipelineBuilder<T1, T2> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }
    public IWorldPipelineBuilder<T1, T2> Parallel() { Builder.MultiThreaded(); return this; }

    public IDisposable Each(PipelineEachAction<T1, T2> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();
        while (it.Next())
        {
            flecs.ecs_iter_t* p = it.Handle;
            ref T1 c1 = ref FlecsIter.First<T1>(p, 0);
            ref T2 c2 = ref FlecsIter.First<T2>(p, 1);
            for (int i = 0, n = p->count; i < n; i++)
                action(dt, new Handle(p->entities[i]), ref Unsafe.Add(ref c1, i), ref Unsafe.Add(ref c2, i));
        }
    }, action);

    public IDisposable Run(PipelineChunkAction<T1, T2> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();
        while (it.Next())
            action(dt, FlecsIter.Entities(it.Handle), FlecsIter.Column<T1>(it.Handle, 0), FlecsIter.Column<T2>(it.Handle, 1));
    }, action);
}

internal sealed unsafe class FlecsSystemBuilder<T1, T2, T3> : FlecsSystemBuilder, IWorldPipelineBuilder<T1, T2, T3>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    public FlecsSystemBuilder(FlecsWorld world, string name, UpdateType phase) : base(world, name, phase) { Builder.With<T1>().With<T2>().With<T3>(); }

    public IWorldPipelineBuilder<T1, T2, T3> With<T>() where T : unmanaged { Builder.With<T>(); return this; }
    public IWorldPipelineBuilder<T1, T2, T3> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }
    public IWorldPipelineBuilder<T1, T2, T3> Parallel() { Builder.MultiThreaded(); return this; }

    public IDisposable Each(PipelineEachAction<T1, T2, T3> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();
        while (it.Next())
        {
            flecs.ecs_iter_t* p = it.Handle;
            ref T1 c1 = ref FlecsIter.First<T1>(p, 0);
            ref T2 c2 = ref FlecsIter.First<T2>(p, 1);
            ref T3 c3 = ref FlecsIter.First<T3>(p, 2);
            for (int i = 0, n = p->count; i < n; i++)
                action(dt, new Handle(p->entities[i]), ref Unsafe.Add(ref c1, i), ref Unsafe.Add(ref c2, i), ref Unsafe.Add(ref c3, i));
        }
    }, action);

    public IDisposable Run(PipelineChunkAction<T1, T2, T3> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();
        while (it.Next())
            action(dt, FlecsIter.Entities(it.Handle), FlecsIter.Column<T1>(it.Handle, 0), FlecsIter.Column<T2>(it.Handle, 1), FlecsIter.Column<T3>(it.Handle, 2));
    }, action);
}

internal sealed unsafe class FlecsSystemBuilder<T1, T2, T3, T4> : FlecsSystemBuilder, IWorldPipelineBuilder<T1, T2, T3, T4>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    public FlecsSystemBuilder(FlecsWorld world, string name, UpdateType phase) : base(world, name, phase) { Builder.With<T1>().With<T2>().With<T3>().With<T4>(); }

    public IWorldPipelineBuilder<T1, T2, T3, T4> With<T>() where T : unmanaged { Builder.With<T>(); return this; }
    public IWorldPipelineBuilder<T1, T2, T3, T4> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }
    public IWorldPipelineBuilder<T1, T2, T3, T4> Parallel() { Builder.MultiThreaded(); return this; }

    public IDisposable Each(PipelineEachAction<T1, T2, T3, T4> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();
        while (it.Next())
        {
            flecs.ecs_iter_t* p = it.Handle;
            ref T1 c1 = ref FlecsIter.First<T1>(p, 0);
            ref T2 c2 = ref FlecsIter.First<T2>(p, 1);
            ref T3 c3 = ref FlecsIter.First<T3>(p, 2);
            ref T4 c4 = ref FlecsIter.First<T4>(p, 3);
            for (int i = 0, n = p->count; i < n; i++)
                action(dt, new Handle(p->entities[i]), ref Unsafe.Add(ref c1, i), ref Unsafe.Add(ref c2, i), ref Unsafe.Add(ref c3, i), ref Unsafe.Add(ref c4, i));
        }
    }, action);

    public IDisposable Run(PipelineChunkAction<T1, T2, T3, T4> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();
        while (it.Next())
            action(dt, FlecsIter.Entities(it.Handle), FlecsIter.Column<T1>(it.Handle, 0), FlecsIter.Column<T2>(it.Handle, 1), FlecsIter.Column<T3>(it.Handle, 2), FlecsIter.Column<T4>(it.Handle, 3));
    }, action);
}
