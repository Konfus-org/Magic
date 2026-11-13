using Magic.Ecs;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.CSharpScripting;

/// <summary>
/// A system whose work on each entity reads and writes only that entity's chunk, so its chunks run at the same time
/// on the worker threads.
/// </summary>
/// <remarks>
/// It runs as any <see cref="ISystem"/> does, in <c>Update</c> each frame, but through
/// <see cref="IEcs.MutateParallel{TState, T1}"/>: <see cref="Run"/> is called once per chunk, and several chunks are
/// in <see cref="Run"/> at the same time. So <see cref="Run"/> writes only through the chunk it gets, and touches
/// neither the world, another chunk, nor state that another chunk's thread writes.
/// </remarks>
public interface IParallelSystem<T1> : ISystem
    where T1 : unmanaged, IComponent
{
    /// <summary>
    /// The delegate that <see cref="IEcs.MutateParallel{TState, T1}"/> gets, made once for the whole interface.
    /// </summary>
    private static readonly EcsChunkRun<Work, EcsMutableChunk<T1>> _runChunk = static (
        in Work work,
        EcsMutableChunk<T1> chunk
    ) => work.System.Run(work.Frame, chunk);

    /// <summary>
    /// Does one frame of the system's work over <paramref name="chunk"/>, on a worker thread.
    /// </summary>
    void Run(in Frame frame, EcsMutableChunk<T1> chunk);

    void ISystem.Run(in Frame frame, World world) => world.MutateParallel(new Work(frame, this), _runChunk);

    /// <summary>
    /// What each chunk's thread needs: the frame, and the copy whose <see cref="Run"/> to call.
    /// </summary>
    private readonly record struct Work(Frame Frame, IParallelSystem<T1> System);
}

/// <summary>
/// <see cref="IParallelSystem{T1}"/> over the entities that have both a <typeparamref name="T1"/> and a
/// <typeparamref name="T2"/>.
/// </summary>
public interface IParallelSystem<T1, T2> : ISystem
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    /// <summary>
    /// The delegate that <see cref="IEcs.MutateParallel{TState, T1, T2}"/> gets, made once for the whole interface.
    /// </summary>
    private static readonly EcsChunkRun<Work, EcsMutableChunk<T1, T2>> _runChunk = static (
        in Work work,
        EcsMutableChunk<T1, T2> chunk
    ) => work.System.Run(work.Frame, chunk);

    /// <summary>
    /// Does one frame of the system's work over <paramref name="chunk"/>, on a worker thread.
    /// </summary>
    void Run(in Frame frame, EcsMutableChunk<T1, T2> chunk);

    void ISystem.Run(in Frame frame, World world) => world.MutateParallel(new Work(frame, this), _runChunk);

    /// <summary>
    /// What each chunk's thread needs: the frame, and the copy whose <see cref="Run"/> to call.
    /// </summary>
    private readonly record struct Work(Frame Frame, IParallelSystem<T1, T2> System);
}
