using System.Runtime.InteropServices;

namespace Magic.Ecs;

/// <summary>
/// A copy of the entities that have a <typeparamref name="T1"/> and their components, as
/// <see cref="EcsExtensions"/>' <c>Snapshot</c> took them: one list each, at the same indexes, that a reader keeps and
/// reads on any thread while the ECS goes on changing. Taking it again reuses the lists.
/// </summary>
public sealed class EcsSnapshot<T1>
    where T1 : unmanaged, IComponent
{
    private readonly List<Entity> _entities = [];
    private readonly List<T1> _first = [];

    public int Count => _entities.Count;

    public ReadOnlySpan<Entity> Entities => CollectionsMarshal.AsSpan(_entities);

    public ReadOnlySpan<T1> First => CollectionsMarshal.AsSpan(_first);

    internal void Clear()
    {
        _entities.Clear();
        _first.Clear();
    }

    internal void Add(EcsChunk<T1> chunk)
    {
        _entities.AddRange(chunk.Entities);
        _first.AddRange(chunk.First);
    }
}

/// <summary>
/// <see cref="EcsSnapshot{T1}"/> of the entities that have both components.
/// </summary>
public sealed class EcsSnapshot<T1, T2>
    where T1 : unmanaged, IComponent
    where T2 : unmanaged, IComponent
{
    private readonly List<Entity> _entities = [];
    private readonly List<T1> _first = [];
    private readonly List<T2> _second = [];

    public int Count => _entities.Count;

    public ReadOnlySpan<Entity> Entities => CollectionsMarshal.AsSpan(_entities);

    public ReadOnlySpan<T1> First => CollectionsMarshal.AsSpan(_first);

    public ReadOnlySpan<T2> Second => CollectionsMarshal.AsSpan(_second);

    internal void Clear()
    {
        _entities.Clear();
        _first.Clear();
        _second.Clear();
    }

    internal void Add(EcsChunk<T1, T2> chunk)
    {
        _entities.AddRange(chunk.Entities);
        _first.AddRange(chunk.First);
        _second.AddRange(chunk.Second);
    }
}
