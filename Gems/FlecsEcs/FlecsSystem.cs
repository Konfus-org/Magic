using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Magic;
using Magic.Contexts;
using Magic.Interfaces;
using System.Runtime.CompilerServices;

namespace FlecsGem;

// A system is a flecs system entity: a query plus a callback, tagged with the phase it runs in. Its run callback
// is registered once and drives the iterator itself, so per frame the only indirection between flecs and the
// caller's code is the caller's own delegate. Parallel() lets flecs split the matched tables across its worker
// threads; each worker calls the run callback with its own share.

internal sealed unsafe class FlecsScheduleBuilder(FlecsEcs ecs, string name) : IEcsPipelineBuilder
{
    private UpdateType _phase = UpdateType.Update;

    public IEcsPipelineBuilder On(UpdateType phase)
    {
        _phase = phase;
        return this;
    }

    public IEcsPipelineBuilder<T1> Query<T1>() where T1 : unmanaged
        => new FlecsSystemBuilder<T1>(ecs, name, _phase);

    public IEcsPipelineBuilder<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged
        => new FlecsSystemBuilder<T1, T2>(ecs, name, _phase);

    public IEcsPipelineBuilder<T1, T2, T3> Query<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
        => new FlecsSystemBuilder<T1, T2, T3>(ecs, name, _phase);

    public IEcsPipelineBuilder<T1, T2, T3, T4> Query<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        => new FlecsSystemBuilder<T1, T2, T3, T4>(ecs, name, _phase);

    public IDisposable Run(Action<float> callback)
    {
        // Immediate: on the main thread with the world writable, and with deferring suspended so what the callback
        // changes (an entity created, a component added) is there the moment it asks for it, as outside a pipeline.
        System_ system = new SystemBuilder(ecs.Native.Handle, name)
            .Kind(ecs.Phases.Tag(_phase))
            .Immediate()
            .Run((Iter it) =>
            {
                float dt = it.DeltaTime();
                ecs.Native.DeferSuspend();
                try
                {
                    callback(dt);
                }
                finally
                {
                    ecs.Native.DeferResume();
                }
            });

        return new FlecsSystem(ecs, system.Entity.Id);
    }
}

/// <summary>
/// A scheduled system. Disposing it destroys the flecs system entity. Whoever scheduled it keeps the handle and
/// disposes it when done (a gem in its own Dispose); otherwise the ECS would keep calling into an unloaded
/// assembly, and keep it alive.
/// </summary>
internal sealed class FlecsSystem : IDisposable
{
    private readonly FlecsEcs _ecs;
    private readonly ulong _id;

    public FlecsSystem(FlecsEcs ecs, ulong id)
    {
        _ecs = ecs;
        _id = id;
    }

    public void Dispose()
    {
        // Flecs.NET.Debug aborts on a dead entity rather than throwing, so check first.
        if (!_ecs.IsDisposed && _ecs.Native.IsAlive(_id))
            _ecs.Native.Entity(_id).Destruct();
    }
}

internal abstract unsafe class FlecsSystemBuilder(FlecsEcs ecs, string name, UpdateType phase)
{
    protected SystemBuilder Builder = new SystemBuilder(ecs.Native.Handle, name).Kind(ecs.Phases.Tag(phase));

    protected FlecsEcs Owner { get; } = ecs;

    protected IDisposable Schedule(Ecs.RunCallback run, Delegate callback)
    {
        return new FlecsSystem(Owner, Builder.Run(run).Entity.Id);
    }
}

internal sealed unsafe class FlecsSystemBuilder<T1> : FlecsSystemBuilder, IEcsPipelineBuilder<T1>
    where T1 : unmanaged
{
    public FlecsSystemBuilder(FlecsEcs ecs, string name, UpdateType phase) : base(ecs, name, phase) { Builder.With<T1>(); }

    public IEcsPipelineBuilder<T1> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsPipelineBuilder<T1> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsPipelineBuilder<T1> Parallel() { Builder.MultiThreaded(); return this; }

    public IDisposable Each(PipelineEachAction<T1> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();

        while (it.Next())
        {
            flecs.ecs_iter_t* iter = it.Handle;
            ref T1 c1 = ref FlecsIter.First<T1>(iter, 0, out int s1);

            for (int i = 0, count = iter->count; i < count; i++)
                action(dt, new Handle(iter->entities[i]), ref Unsafe.Add(ref c1, i * s1));
        }
    }, action);

    public IDisposable Run(PipelineChunkAction<T1> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();

        while (it.Next())
            action(dt, FlecsIter.Entities(it.Handle), FlecsIter.Column<T1>(it.Handle, 0));
    }, action);
}

internal sealed unsafe class FlecsSystemBuilder<T1, T2> : FlecsSystemBuilder, IEcsPipelineBuilder<T1, T2>
    where T1 : unmanaged where T2 : unmanaged
{
    public FlecsSystemBuilder(FlecsEcs ecs, string name, UpdateType phase) : base(ecs, name, phase) { Builder.With<T1>().With<T2>(); }

    public IEcsPipelineBuilder<T1, T2> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsPipelineBuilder<T1, T2> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsPipelineBuilder<T1, T2> Parallel() { Builder.MultiThreaded(); return this; }

    public IDisposable Each(PipelineEachAction<T1, T2> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();

        while (it.Next())
        {
            flecs.ecs_iter_t* iter = it.Handle;
            ref T1 c1 = ref FlecsIter.First<T1>(iter, 0, out int s1);
            ref T2 c2 = ref FlecsIter.First<T2>(iter, 1, out int s2);

            for (int i = 0, count = iter->count; i < count; i++)
                action(dt, new Handle(iter->entities[i]), ref Unsafe.Add(ref c1, i * s1), ref Unsafe.Add(ref c2, i * s2));
        }
    }, action);

    public IDisposable Run(PipelineChunkAction<T1, T2> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();

        while (it.Next())
            action(dt, FlecsIter.Entities(it.Handle), FlecsIter.Column<T1>(it.Handle, 0), FlecsIter.Column<T2>(it.Handle, 1));
    }, action);
}

internal sealed unsafe class FlecsSystemBuilder<T1, T2, T3> : FlecsSystemBuilder, IEcsPipelineBuilder<T1, T2, T3>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    public FlecsSystemBuilder(FlecsEcs ecs, string name, UpdateType phase) : base(ecs, name, phase) { Builder.With<T1>().With<T2>().With<T3>(); }

    public IEcsPipelineBuilder<T1, T2, T3> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsPipelineBuilder<T1, T2, T3> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsPipelineBuilder<T1, T2, T3> Parallel() { Builder.MultiThreaded(); return this; }

    public IDisposable Each(PipelineEachAction<T1, T2, T3> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();

        while (it.Next())
        {
            flecs.ecs_iter_t* iter = it.Handle;
            ref T1 c1 = ref FlecsIter.First<T1>(iter, 0, out int s1);
            ref T2 c2 = ref FlecsIter.First<T2>(iter, 1, out int s2);
            ref T3 c3 = ref FlecsIter.First<T3>(iter, 2, out int s3);

            for (int i = 0, count = iter->count; i < count; i++)
            {
                action(
                    dt,
                    new Handle(iter->entities[i]),
                    ref Unsafe.Add(ref c1, i * s1),
                    ref Unsafe.Add(ref c2, i * s2),
                    ref Unsafe.Add(ref c3, i * s3));
            }
        }
    }, action);

    public IDisposable Run(PipelineChunkAction<T1, T2, T3> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();

        while (it.Next())
        {
            action(
                dt,
                FlecsIter.Entities(it.Handle),
                FlecsIter.Column<T1>(it.Handle, 0),
                FlecsIter.Column<T2>(it.Handle, 1),
                FlecsIter.Column<T3>(it.Handle, 2));
        }
    }, action);
}

internal sealed unsafe class FlecsSystemBuilder<T1, T2, T3, T4> : FlecsSystemBuilder, IEcsPipelineBuilder<T1, T2, T3, T4>
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    public FlecsSystemBuilder(FlecsEcs ecs, string name, UpdateType phase) : base(ecs, name, phase) { Builder.With<T1>().With<T2>().With<T3>().With<T4>(); }

    public IEcsPipelineBuilder<T1, T2, T3, T4> With<T>() where T : unmanaged { Builder.With<T>(); return this; }

    public IEcsPipelineBuilder<T1, T2, T3, T4> Without<T>() where T : unmanaged { Builder.Without<T>(); return this; }

    public IEcsPipelineBuilder<T1, T2, T3, T4> Parallel() { Builder.MultiThreaded(); return this; }

    public IDisposable Each(PipelineEachAction<T1, T2, T3, T4> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();

        while (it.Next())
        {
            flecs.ecs_iter_t* iter = it.Handle;
            ref T1 c1 = ref FlecsIter.First<T1>(iter, 0, out int s1);
            ref T2 c2 = ref FlecsIter.First<T2>(iter, 1, out int s2);
            ref T3 c3 = ref FlecsIter.First<T3>(iter, 2, out int s3);
            ref T4 c4 = ref FlecsIter.First<T4>(iter, 3, out int s4);

            for (int i = 0, count = iter->count; i < count; i++)
            {
                action(
                    dt,
                    new Handle(iter->entities[i]),
                    ref Unsafe.Add(ref c1, i * s1),
                    ref Unsafe.Add(ref c2, i * s2),
                    ref Unsafe.Add(ref c3, i * s3),
                    ref Unsafe.Add(ref c4, i * s4));
            }
        }
    }, action);

    public IDisposable Run(PipelineChunkAction<T1, T2, T3, T4> action) => Schedule((Iter it) =>
    {
        float dt = it.DeltaTime();

        while (it.Next())
        {
            action(
                dt,
                FlecsIter.Entities(it.Handle),
                FlecsIter.Column<T1>(it.Handle, 0),
                FlecsIter.Column<T2>(it.Handle, 1),
                FlecsIter.Column<T3>(it.Handle, 2),
                FlecsIter.Column<T4>(it.Handle, 3));
        }
    }, action);
}
