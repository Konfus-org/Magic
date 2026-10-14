using Magic.Interfaces;

namespace Magic.Contexts.ECS;

/// <summary>
/// Entities the ECS stores together, as an <see cref="EcsQuery"/> or an <see cref="IParallelSystem"/> is handed them,
/// over the ECS's own storage. Good until that loop or call ends.
/// </summary>
public readonly ref struct EcsChunk(ReadOnlySpan<Entity> entities)
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;
}

/// <summary>
/// <see cref="EcsChunk"/> of entities that have a <typeparamref name="T1"/>, with theirs at the same indexes: writing
/// through <see cref="First"/> changes the components.
/// </summary>
public readonly ref struct EcsChunk<T1>(ReadOnlySpan<Entity> entities, Span<T1> first)
    where T1 : unmanaged, IComponent
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;

    public Span<T1> First { get; } = first;

    public void Deconstruct(out ReadOnlySpan<Entity> entities, out Span<T1> first)
    {
        entities = Entities;
        first = First;
    }
}

/// <summary>
/// <see cref="EcsChunk{T1}"/> of entities that have both components.
/// </summary>
public readonly ref struct EcsChunk<T1, T2>(ReadOnlySpan<Entity> entities, Span<T1> first, Span<T2> second)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;

    public Span<T1> First { get; } = first;

    public Span<T2> Second { get; } = second;

    public void Deconstruct(out ReadOnlySpan<Entity> entities, out Span<T1> first, out Span<T2> second)
    {
        entities = Entities;
        first = First;
        second = Second;
    }
}

/// <summary>
/// <see cref="EcsChunk{T1}"/> of entities that have all three components.
/// </summary>
public readonly ref struct EcsChunk<T1, T2, T3>(ReadOnlySpan<Entity> entities, Span<T1> first, Span<T2> second, Span<T3> third)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;

    public Span<T1> First { get; } = first;

    public Span<T2> Second { get; } = second;

    public Span<T3> Third { get; } = third;

    public void Deconstruct(out ReadOnlySpan<Entity> entities, out Span<T1> first, out Span<T2> second, out Span<T3> third)
    {
        entities = Entities;
        first = First;
        second = Second;
        third = Third;
    }
}

/// <summary>
/// <see cref="EcsChunk{T1}"/> of entities that have all four components.
/// </summary>
public readonly ref struct EcsChunk<T1, T2, T3, T4>(ReadOnlySpan<Entity> entities, Span<T1> first, Span<T2> second, Span<T3> third, Span<T4> fourth)
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
    where T4 : unmanaged, IComponent
{
    public ReadOnlySpan<Entity> Entities { get; } = entities;

    public Span<T1> First { get; } = first;

    public Span<T2> Second { get; } = second;

    public Span<T3> Third { get; } = third;

    public Span<T4> Fourth { get; } = fourth;

    public void Deconstruct(out ReadOnlySpan<Entity> entities, out Span<T1> first, out Span<T2> second, out Span<T3> third, out Span<T4> fourth)
    {
        entities = Entities;
        first = First;
        second = Second;
        third = Third;
        fourth = Fourth;
    }
}
