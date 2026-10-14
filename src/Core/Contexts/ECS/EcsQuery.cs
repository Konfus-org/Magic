using Magic.Interfaces;

namespace Magic.Contexts.ECS;

/// <summary>
/// Every entity, as the ECS hands them to an <see cref="ISystem"/> or <see cref="IEcs.Query()"/> returns them:
/// <c>foreach</c> over it for each <see cref="EcsChunk"/>, the entities stored together. The ECS keeps entities in
/// chunks, each its own storage, so no one span covers them all. The chunks are gathered as the loop starts, and no
/// entity's components can be added or taken off until it ends (<see cref="IEcs"/>).
/// </summary>
/// <param name="chunks">The ECS gem's walk over its chunks.</param>
public readonly ref struct EcsQuery(IEnumerable<EcsChunk> chunks)
{
    public IEnumerator<EcsChunk> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsQuery"/> over the entities that have a <typeparamref name="T1"/>.
/// </summary>
public readonly ref struct EcsQuery<T1>(IEnumerable<EcsChunk<T1>> chunks)
    where T1 : unmanaged, IComponent
{
    public IEnumerator<EcsChunk<T1>> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsQuery"/> over the entities that have both components.
/// </summary>
public readonly ref struct EcsQuery<T1, T2>(IEnumerable<EcsChunk<T1, T2>> chunks)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    public IEnumerator<EcsChunk<T1, T2>> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsQuery"/> over the entities that have all three components.
/// </summary>
public readonly ref struct EcsQuery<T1, T2, T3>(IEnumerable<EcsChunk<T1, T2, T3>> chunks)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
{
    public IEnumerator<EcsChunk<T1, T2, T3>> GetEnumerator() => chunks.GetEnumerator();
}

/// <summary>
/// <see cref="EcsQuery"/> over the entities that have all four components.
/// </summary>
public readonly ref struct EcsQuery<T1, T2, T3, T4>(IEnumerable<EcsChunk<T1, T2, T3, T4>> chunks)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
    where T4 : unmanaged, IComponent
{
    public IEnumerator<EcsChunk<T1, T2, T3, T4>> GetEnumerator() => chunks.GetEnumerator();
}
