namespace Magic.Ecs;

/// <summary>
/// A system's work on one chunk of entities, an <see cref="EcsChunk"/> or one of its kinds with components, as
/// <see cref="IEcs.Run{TState}"/> calls it on a worker thread, handed <paramref name="state"/>.
/// </summary>
public delegate void EcsChunkRun<TState, TChunk>(in TState state, TChunk chunk)
    where TChunk : allows ref struct;
