namespace Magic.Ecs;

/// <summary>
/// Entities that the ECS stores together, as an <see cref="EcsQuery"/> gives them out, over the storage of the ECS.
/// </summary>
/// <remarks>
/// Use it only until the loop of that query ends.
/// </remarks>
public readonly ref struct EcsChunk(ReadOnlySpan<Entity> entities)
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;
}

/// <summary>
/// <see cref="EcsChunk"/> of entities that have a <typeparamref name="T1"/>, with their components at the same indexes,
/// to read.
/// </summary>
/// <remarks>
/// <see cref="Written"/> is the version at which the chunk was last written. It is above the
/// <see cref="EcsQuery{T1}.Version"/> of a query when the chunk was written after that query.
/// </remarks>
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
/// <see cref="EcsChunk{T1}"/> to write, as an <see cref="EcsMutation{T1}"/> gives it out.
/// </summary>
/// <remarks>
/// A write through <see cref="First"/> changes the components. The chunk is marked written when it is given out, also
/// when nothing is written.
/// </remarks>
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
