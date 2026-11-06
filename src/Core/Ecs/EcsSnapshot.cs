namespace Magic.Ecs;

/// <summary>
/// A copy of the entities that have a <typeparamref name="T1"/> and their components, as
/// <see cref="EcsExtensions"/>' <c>Snapshot</c> took them: one array each, at the same indexes, that a reader keeps and
/// reads on any thread while the ECS goes on changing. Taking it again reuses the arrays.
/// </summary>
public sealed class EcsSnapshot<T1>
    where T1 : unmanaged, IComponent
{
    private Entity[] _entities = [];
    private T1[] _first = [];

    public int Count { get; private set; }

    public ReadOnlySpan<Entity> Entities => _entities.AsSpan(0, Count);

    public ReadOnlySpan<T1> First => _first.AsSpan(0, Count);

    internal void Clear() => Count = 0;

    internal void Add(EcsChunk<T1> chunk)
    {
        int count = Count + chunk.Entities.Length;
        EcsSnapshot.Copy(chunk.Entities, ref _entities, Count);
        EcsSnapshot.Copy(chunk.First, ref _first, Count);
        Count = count;
    }
}

/// <summary>
/// <see cref="EcsSnapshot{T1}"/> of the entities that have both components.
/// </summary>
public sealed class EcsSnapshot<T1, T2>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    private Entity[] _entities = [];
    private T1[] _first = [];
    private T2[] _second = [];

    public int Count { get; private set; }

    public ReadOnlySpan<Entity> Entities => _entities.AsSpan(0, Count);

    public ReadOnlySpan<T1> First => _first.AsSpan(0, Count);

    public ReadOnlySpan<T2> Second => _second.AsSpan(0, Count);

    internal void Clear() => Count = 0;

    internal void Add(EcsChunk<T1, T2> chunk)
    {
        int count = Count + chunk.Entities.Length;
        EcsSnapshot.Copy(chunk.Entities, ref _entities, Count);
        EcsSnapshot.Copy(chunk.First, ref _first, Count);
        EcsSnapshot.Copy(chunk.Second, ref _second, Count);
        Count = count;
    }
}

/// <summary>
/// <see cref="EcsSnapshot{T1}"/> of the entities that have all three components.
/// </summary>
public sealed class EcsSnapshot<T1, T2, T3>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
{
    private Entity[] _entities = [];
    private T1[] _first = [];
    private T2[] _second = [];
    private T3[] _third = [];

    public int Count { get; private set; }

    public ReadOnlySpan<Entity> Entities => _entities.AsSpan(0, Count);

    public ReadOnlySpan<T1> First => _first.AsSpan(0, Count);

    public ReadOnlySpan<T2> Second => _second.AsSpan(0, Count);

    public ReadOnlySpan<T3> Third => _third.AsSpan(0, Count);

    internal void Clear() => Count = 0;

    internal void Add(EcsChunk<T1, T2, T3> chunk)
    {
        int count = Count + chunk.Entities.Length;
        EcsSnapshot.Copy(chunk.Entities, ref _entities, Count);
        EcsSnapshot.Copy(chunk.First, ref _first, Count);
        EcsSnapshot.Copy(chunk.Second, ref _second, Count);
        EcsSnapshot.Copy(chunk.Third, ref _third, Count);
        Count = count;
    }
}

/// <summary>
/// <see cref="EcsSnapshot{T1}"/> of the entities that have all four components.
/// </summary>
public sealed class EcsSnapshot<T1, T2, T3, T4>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
    where T3 : unmanaged, IComponent
    where T4 : unmanaged, IComponent
{
    private Entity[] _entities = [];
    private T1[] _first = [];
    private T2[] _second = [];
    private T3[] _third = [];
    private T4[] _fourth = [];

    public int Count { get; private set; }

    public ReadOnlySpan<Entity> Entities => _entities.AsSpan(0, Count);

    public ReadOnlySpan<T1> First => _first.AsSpan(0, Count);

    public ReadOnlySpan<T2> Second => _second.AsSpan(0, Count);

    public ReadOnlySpan<T3> Third => _third.AsSpan(0, Count);

    public ReadOnlySpan<T4> Fourth => _fourth.AsSpan(0, Count);

    internal void Clear() => Count = 0;

    internal void Add(EcsChunk<T1, T2, T3, T4> chunk)
    {
        int count = Count + chunk.Entities.Length;
        EcsSnapshot.Copy(chunk.Entities, ref _entities, Count);
        EcsSnapshot.Copy(chunk.First, ref _first, Count);
        EcsSnapshot.Copy(chunk.Second, ref _second, Count);
        EcsSnapshot.Copy(chunk.Third, ref _third, Count);
        EcsSnapshot.Copy(chunk.Fourth, ref _fourth, Count);
        Count = count;
    }
}

/// <summary>
/// What every snapshot does with its arrays.
/// </summary>
internal static class EcsSnapshot
{
    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="into"/> from <paramref name="at"/>, first doubling the array
    /// until it fits, keeping what it holds.
    /// </summary>
    public static void Copy<T>(ReadOnlySpan<T> source, ref T[] into, int at)
    {
        int needed = at + source.Length;
        if (needed > into.Length)
            Array.Resize(ref into, Math.Max(needed, into.Length * 2));

        source.CopyTo(into.AsSpan(at));
    }
}
