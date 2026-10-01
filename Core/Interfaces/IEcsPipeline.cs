using Magic.Contexts;

namespace Magic.Interfaces;

/// <summary>
/// The phases of a frame, in order: the <see cref="IGem"/> hook an ECS pipeline runs in. Everything that produces
/// frame state (movement, animation, the transform hierarchy) runs by <see cref="LateUpdate"/>; <see cref="Render"/>
/// consumes it.
/// </summary>
public enum UpdateType
{
    Update,
    FixedUpdate,
    LateUpdate,
    Render
}

public interface IEcsPipelineBuilder
{
    /// <summary>The phase the system runs in. Defaults to <see cref="UpdateType.Update"/>.</summary>
    IEcsPipelineBuilder On(UpdateType phase);

    IEcsPipelineBuilder<T1> Query<T1>() where T1 : unmanaged;

    IEcsPipelineBuilder<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged;

    IEcsPipelineBuilder<T1, T2, T3> Query<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;

    IEcsPipelineBuilder<T1, T2, T3, T4> Query<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged;

    /// <summary>A system without a query: a plain callback with the phase's delta time in seconds.</summary>
    IDisposable Run(Action<float> callback);
}

// Parallel(): the callback is invoked from worker threads, several chunks at once. It must be thread safe and
// must not touch the world structurally (create, destroy, add, remove) or any host service, except members
// explicitly documented as callable from any thread (today Assets.Find, PathOf, Load and LoadAsync).

public interface IEcsPipelineBuilder<T1> where T1 : unmanaged
{
    IEcsPipelineBuilder<T1> With<T>() where T : unmanaged;

    IEcsPipelineBuilder<T1> Without<T>() where T : unmanaged;

    IEcsPipelineBuilder<T1> Parallel();

    IDisposable Each(PipelineEachAction<T1> action);

    IDisposable Run(PipelineChunkAction<T1> action);
}

public interface IEcsPipelineBuilder<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    IEcsPipelineBuilder<T1, T2> With<T>() where T : unmanaged;

    IEcsPipelineBuilder<T1, T2> Without<T>() where T : unmanaged;

    IEcsPipelineBuilder<T1, T2> Parallel();

    IDisposable Each(PipelineEachAction<T1, T2> action);

    IDisposable Run(PipelineChunkAction<T1, T2> action);
}

public interface IEcsPipelineBuilder<T1, T2, T3> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    IEcsPipelineBuilder<T1, T2, T3> With<T>() where T : unmanaged;

    IEcsPipelineBuilder<T1, T2, T3> Without<T>() where T : unmanaged;

    IEcsPipelineBuilder<T1, T2, T3> Parallel();

    IDisposable Each(PipelineEachAction<T1, T2, T3> action);

    IDisposable Run(PipelineChunkAction<T1, T2, T3> action);
}

public interface IEcsPipelineBuilder<T1, T2, T3, T4> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    IEcsPipelineBuilder<T1, T2, T3, T4> With<T>() where T : unmanaged;

    IEcsPipelineBuilder<T1, T2, T3, T4> Without<T>() where T : unmanaged;

    IEcsPipelineBuilder<T1, T2, T3, T4> Parallel();

    IDisposable Each(PipelineEachAction<T1, T2, T3, T4> action);

    IDisposable Run(PipelineChunkAction<T1, T2, T3, T4> action);
}

public delegate void PipelineEachAction<T1>(float dt, Handle entity, ref T1 c1)
    where T1 : unmanaged;

public delegate void PipelineEachAction<T1, T2>(float dt, Handle entity, ref T1 c1, ref T2 c2)
    where T1 : unmanaged where T2 : unmanaged;

public delegate void PipelineEachAction<T1, T2, T3>(float dt, Handle entity, ref T1 c1, ref T2 c2, ref T3 c3)
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;

public delegate void PipelineEachAction<T1, T2, T3, T4>(float dt, Handle entity, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4)
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged;

public delegate void PipelineChunkAction<T1>(float dt, ReadOnlySpan<Handle> entities, Span<T1> c1)
    where T1 : unmanaged;

public delegate void PipelineChunkAction<T1, T2>(float dt, ReadOnlySpan<Handle> entities, Span<T1> c1, Span<T2> c2)
    where T1 : unmanaged where T2 : unmanaged;

public delegate void PipelineChunkAction<T1, T2, T3>(float dt, ReadOnlySpan<Handle> entities, Span<T1> c1, Span<T2> c2, Span<T3> c3)
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;

public delegate void PipelineChunkAction<T1, T2, T3, T4>(float dt, ReadOnlySpan<Handle> entities, Span<T1> c1, Span<T2> c2, Span<T3> c3, Span<T4> c4)
    where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged;
