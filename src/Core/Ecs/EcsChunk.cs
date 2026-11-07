namespace Magic.Ecs;

/// <summary>
/// Entities the ECS stores together, as an <see cref="EcsQuery"/> hands them out, over the ECS's own storage. Good
/// until that loop ends.
/// </summary>
public readonly ref struct EcsChunk(ReadOnlySpan<Entity> entities)
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;
}

/// <summary>
/// <see cref="EcsChunk"/> of entities that have a <typeparamref name="T1"/>, with theirs at the same indexes, to read.
/// <see cref="Written"/> is when the chunk was last written: above a query's <see cref="EcsQuery{T1}.Version"/> when
/// it was written after that query.
/// </summary>
public readonly ref struct EcsChunk<T1>(ReadOnlySpan<Entity> entities, ReadOnlySpan<T1> first, long written)
    where T1 : unmanaged, IComponent
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;

    public ReadOnlySpan<T1> First { get; } = first;

    public long Written { get; } = written;
}

/// <summary>
/// <see cref="EcsChunk{T1}"/> of entities that have both components.
/// </summary>
public readonly ref struct EcsChunk<T1, T2>(
    ReadOnlySpan<Entity> entities,
    ReadOnlySpan<T1> first,
    ReadOnlySpan<T2> second,
    long written
)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;

    public ReadOnlySpan<T1> First { get; } = first;

    public ReadOnlySpan<T2> Second { get; } = second;

    public long Written { get; } = written;
}

/// <summary>
/// <see cref="EcsChunk{T1}"/> to write, as an <see cref="EcsMutation{T1}"/> hands it out: writing through
/// <see cref="First"/> changes the components. Handing it out marks the chunk written, whether or not anything is.
/// </summary>
public readonly ref struct EcsMutableChunk<T1>(ReadOnlySpan<Entity> entities, Span<T1> first)
    where T1 : unmanaged, IComponent
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;

    public Span<T1> First { get; } = first;
}

/// <summary>
/// <see cref="EcsMutableChunk{T1}"/> of entities that have both components.
/// </summary>
public readonly ref struct EcsMutableChunk<T1, T2>(ReadOnlySpan<Entity> entities, Span<T1> first, Span<T2> second)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;

    public Span<T1> First { get; } = first;

    public Span<T2> Second { get; } = second;
}
