using System.Runtime.InteropServices;

namespace Magic.Ecs;

/// <summary>
/// A copy of the entities that have a <typeparamref name="T1"/> and their components, as
/// <see cref="EcsExtensions"/>' <c>Snapshot</c> took them: one list each, at the same indexes, that a reader keeps and
/// reads on any thread while the ECS goes on changing. Taking it again copies, into the same lists, only the chunks
/// written since or that moved.
/// </summary>
public sealed class EcsSnapshot<T1>
    where T1 : unmanaged, IComponent
{
    private readonly List<Entity> _entities = [];
    private readonly List<T1> _first = [];
    private readonly EcsSnapshotChunks _chunks = new();

    public int Count => _entities.Count;

    public ReadOnlySpan<Entity> Entities => CollectionsMarshal.AsSpan(_entities);

    public ReadOnlySpan<T1> First => CollectionsMarshal.AsSpan(_first);

    internal void Start() => _chunks.Start();

    internal void Add(EcsChunk<T1> chunk)
    {
        if (_chunks.IsHeld(chunk.Entities, chunk.Written, out int offset))
            return;

        EcsSnapshotChunks.Copy(_entities, offset, chunk.Entities);
        EcsSnapshotChunks.Copy(_first, offset, chunk.First);
    }

    internal void End(long version)
    {
        int count = _chunks.End(version);
        CollectionsMarshal.SetCount(_entities, count);
        CollectionsMarshal.SetCount(_first, count);
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
    private readonly EcsSnapshotChunks _chunks = new();

    public int Count => _entities.Count;

    public ReadOnlySpan<Entity> Entities => CollectionsMarshal.AsSpan(_entities);

    public ReadOnlySpan<T1> First => CollectionsMarshal.AsSpan(_first);

    public ReadOnlySpan<T2> Second => CollectionsMarshal.AsSpan(_second);

    internal void Start() => _chunks.Start();

    internal void Add(EcsChunk<T1, T2> chunk)
    {
        if (_chunks.IsHeld(chunk.Entities, chunk.Written, out int offset))
            return;

        EcsSnapshotChunks.Copy(_entities, offset, chunk.Entities);
        EcsSnapshotChunks.Copy(_first, offset, chunk.First);
        EcsSnapshotChunks.Copy(_second, offset, chunk.Second);
    }

    internal void End(long version)
    {
        int count = _chunks.End(version);
        CollectionsMarshal.SetCount(_entities, count);
        CollectionsMarshal.SetCount(_first, count);
        CollectionsMarshal.SetCount(_second, count);
    }
}

/// <summary>
/// Where each chunk a snapshot holds was copied to, so taking it again copies only the chunks written after the last
/// fill's version or that moved. The next chunk is what the lists hold there already when it starts at the same place
/// with the same entity, has as many entities, and was written no later: an entity joins, leaves or moves within a
/// chunk only by a write, which marks it.
/// </summary>
internal sealed class EcsSnapshotChunks
{
    private readonly List<(int Offset, int Count, Entity First)> _held = [];

    /// <summary>
    /// The version the last fill read at.
    /// </summary>
    private long _heldAt;

    private int _index;

    private int _count;

    public static void Copy<T>(List<T> list, int offset, ReadOnlySpan<T> values)
    {
        if (list.Count < offset + values.Length)
            CollectionsMarshal.SetCount(list, offset + values.Length);

        values.CopyTo(CollectionsMarshal.AsSpan(list)[offset..]);
    }

    public void Start() => (_index, _count) = (0, 0);

    /// <summary>
    /// Whether the lists hold the next chunk as it is, the one of <paramref name="entities"/> last written at
    /// <paramref name="written"/>, with the place it takes in <paramref name="offset"/>. An empty chunk takes none.
    /// </summary>
    public bool IsHeld(ReadOnlySpan<Entity> entities, long written, out int offset)
    {
        offset = _count;
        if (entities.IsEmpty)
            return true;

        (int, int, Entity) chunk = (offset, entities.Length, entities[0]);
        bool isHeld = _index < _held.Count && _held[_index] == chunk && written <= _heldAt;
        if (_index < _held.Count)
            _held[_index] = chunk;
        else
            _held.Add(chunk);

        _index++;
        _count += entities.Length;

        return isHeld;
    }

    /// <summary>
    /// How many entities the fill placed, the fill having read at <paramref name="version"/>.
    /// </summary>
    public int End(long version)
    {
        _held.RemoveRange(_index, _held.Count - _index);
        _heldAt = version;

        return _count;
    }
}
