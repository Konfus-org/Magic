namespace Magic.Ecs;

/// <summary>
/// Every entity, as <see cref="IEcs.Query()"/> returns them: <c>foreach</c> over it for each <see cref="EcsChunk"/>,
/// the entities stored together. The ECS keeps entities in chunks, each its own storage, so no one span covers them
/// all. The chunks are gathered as the loop starts, and no entity's components can be added or taken off until it
/// ends.
/// </summary>
/// <param name="chunks">The ECS gem's walk over its chunks.</param>
public readonly ref struct EcsQuery(IEnumerable<EcsChunk> chunks)
{
    public IEnumerator<EcsChunk> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsQuery"/> over the entities that have a <typeparamref name="T1"/>, to read. A chunk written after the
/// query has a <see cref="EcsChunk{T1}.Written"/> above its <see cref="Version"/>, so a reader that keeps the version
/// can read again only what was written since.
/// </summary>
public readonly ref struct EcsQuery<T1>(IEnumerable<EcsChunk<T1>> chunks, long version)
    where T1 : unmanaged, IComponent
{
    public long Version { get; } = version;

    public IEnumerator<EcsChunk<T1>> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsQuery{T1}"/> over the entities that have both components.
/// </summary>
public readonly ref struct EcsQuery<T1, T2>(IEnumerable<EcsChunk<T1, T2>> chunks, long version)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    public long Version { get; } = version;

    public IEnumerator<EcsChunk<T1, T2>> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsQuery{T1}"/> to write, as <see cref="IEcs.Mutate{T1}()"/> returns it: every chunk it walks is marked
/// written.
/// </summary>
public readonly ref struct EcsMutation<T1>(IEnumerable<EcsMutableChunk<T1>> chunks)
    where T1 : unmanaged, IComponent
{
    public IEnumerator<EcsMutableChunk<T1>> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsMutation{T1}"/> over the entities that have both components.
/// </summary>
public readonly ref struct EcsMutation<T1, T2>(IEnumerable<EcsMutableChunk<T1, T2>> chunks)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    public IEnumerator<EcsMutableChunk<T1, T2>> GetEnumerator() => chunks.GetEnumerator();
}
