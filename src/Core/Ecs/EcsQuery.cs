namespace Magic.Ecs;

/// <summary>
/// Every entity, chunk by chunk, as <see cref="IEcs.Query()"/> returns them.
/// </summary>
/// <remarks>
/// Use <c>foreach</c> to get each <see cref="EcsChunk"/>. Each chunk has its own storage, so no one span covers all
/// the entities. The loop gathers the chunks when it starts. Do not add or remove the components of an entity until
/// the loop ends.
/// </remarks>
/// <param name="chunks">The ECS gem's walk over its chunks.</param>
public readonly ref struct EcsQuery(IEnumerable<EcsChunk> chunks)
{
    public IEnumerator<EcsChunk> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsQuery"/> over the entities that have a <typeparamref name="T1"/>, to read.
/// </summary>
/// <remarks>
/// A chunk written after the query has an <see cref="EcsChunk{T1}.Written"/> above its <see cref="Version"/>. A reader
/// that keeps the version can read again only what was written since.
/// </remarks>
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
