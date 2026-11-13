using System.Runtime.InteropServices;

namespace Magic.Ecs;

/// <summary>
/// A copy of the entities that have a <typeparamref name="T1"/> and their components, which a reader keeps and reads on
/// any thread.
/// </summary>
/// <remarks>
/// <see cref="Fill"/> copies them into one list each, at the same indexes, while the ECS continues to change. A new
/// fill copies into the same lists only the chunks written or moved since the last fill.
/// </remarks>
public sealed class EcsSnapshot<T1>
    where T1 : unmanaged, IComponent
{
    private readonly List<Entity> _entities = [];
    private readonly List<T1> _first = [];
    private readonly EcsSnapshotChunks _chunks = new();

    public int Count => _entities.Count;

    public ReadOnlySpan<Entity> Entities => CollectionsMarshal.AsSpan(_entities);

    public ReadOnlySpan<T1> First => CollectionsMarshal.AsSpan(_first);

    /// <summary>
    /// Replaces the copy with the entities of <paramref name="ecs"/> that have a <typeparamref name="T1"/> and their
    /// components, in query order.
    /// </summary>
    /// <remarks>
    /// The fill runs on this thread and copies only the chunks written since the last fill, so a world that does not
    /// change copies nothing. After the fill, any thread can read the copy.
    /// </remarks>
    public void Fill(IEcs ecs)
    {
        ArgumentNullException.ThrowIfNull(ecs);
        EcsQuery<T1> query = ecs.Query<T1>();

        _chunks.Start();
        foreach (EcsChunk<T1> chunk in query)
        {
            if (_chunks.IsHeld(chunk.Entities, chunk.Written, out int offset))
                continue;

            EcsSnapshotChunks.Copy(_entities, offset, chunk.Entities);
            EcsSnapshotChunks.Copy(_first, offset, chunk.First);
        }

        int count = _chunks.End(query.Version);
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

    /// <summary>
    /// Where each chunk of entities sits in the lists, in query order, and when it was last written.
    /// </summary>
    public ReadOnlySpan<EcsSnapshotChunk> Chunks => _chunks.Held;

    /// <summary>
    /// The version at which the last fill read.
    /// </summary>
    /// <remarks>
    /// A chunk whose <see cref="EcsSnapshotChunk.Written"/> is not higher was not written after that fill.
    /// </remarks>
    public long Version => _chunks.Version;

    /// <summary>
    /// <see cref="EcsSnapshot{T1}.Fill"/> with the entities that have both components.
    /// </summary>
    public void Fill(IEcs ecs)
    {
        ArgumentNullException.ThrowIfNull(ecs);
        EcsQuery<T1, T2> query = ecs.Query<T1, T2>();

        _chunks.Start();
        foreach (EcsChunk<T1, T2> chunk in query)
        {
            if (_chunks.IsHeld(chunk.Entities, chunk.Written, out int offset))
                continue;

            EcsSnapshotChunks.Copy(_entities, offset, chunk.Entities);
            EcsSnapshotChunks.Copy(_first, offset, chunk.First);
            EcsSnapshotChunks.Copy(_second, offset, chunk.Second);
        }

        int count = _chunks.End(query.Version);
        CollectionsMarshal.SetCount(_entities, count);
        CollectionsMarshal.SetCount(_first, count);
        CollectionsMarshal.SetCount(_second, count);
    }
}

/// <summary>
/// Records where each chunk of a snapshot was copied, so that the next fill copies only the chunks written or moved.
/// </summary>
/// <remarks>
/// The lists already hold the next chunk when it starts at the same place with the same entity, has as many entities,
/// and was not written later. An entity joins, leaves or moves in a chunk only by a write, which marks the chunk.
/// </remarks>
internal sealed class EcsSnapshotChunks
{
    private readonly List<EcsSnapshotChunk> _held = [];

    private int _index;

    private int _count;

    public ReadOnlySpan<EcsSnapshotChunk> Held => CollectionsMarshal.AsSpan(_held);

    /// <summary>
    /// The version the last fill read at.
    /// </summary>
    public long Version { get; private set; }

    public static void Copy<T>(List<T> list, int offset, ReadOnlySpan<T> values)
    {
        if (list.Count < offset + values.Length)
            CollectionsMarshal.SetCount(list, offset + values.Length);

        values.CopyTo(CollectionsMarshal.AsSpan(list)[offset..]);
    }

    public void Start() => (_index, _count) = (0, 0);

    /// <summary>
    /// Records the next chunk, of <paramref name="entities"/> last written at <paramref name="written"/>, and returns
    /// whether the lists already hold it.
    /// </summary>
    /// <remarks>
    /// <paramref name="offset"/> gets the place of the chunk in the lists. An empty chunk takes no place.
    /// </remarks>
    public bool IsHeld(ReadOnlySpan<Entity> entities, long written, out int offset)
    {
        offset = _count;
        if (entities.IsEmpty)
            return true;

        EcsSnapshotChunk chunk = new(offset, entities.Length, entities[0], written);
        bool isHeld = _index < _held.Count && _held[_index].IsSameChunk(chunk) && written <= Version;
        if (_index < _held.Count)
            _held[_index] = chunk;
        else
            _held.Add(chunk);

        _index++;
        _count += entities.Length;

        return isHeld;
    }

    /// <summary>
    /// Ends the fill that read at <paramref name="version"/>, and returns how many entities it placed.
    /// </summary>
    public int End(long version)
    {
        _held.RemoveRange(_index, _held.Count - _index);
        Version = version;

        return _count;
    }
}

/// <summary>
/// A chunk of entities as a snapshot holds it: from <paramref name="Offset"/> in its lists, <paramref name="Count"/>
/// entities from <paramref name="First"/>, last written at <paramref name="Written"/>.
/// </summary>
public readonly record struct EcsSnapshotChunk(int Offset, int Count, Entity First, long Written)
{
    /// <summary>
    /// Whether <paramref name="other"/> is this chunk in the same place, at any version.
    /// </summary>
    /// <remarks>
    /// An entity joins, leaves or moves in a chunk only by a write, so the same first entity at the same place is the
    /// same chunk.
    /// </remarks>
    public bool IsSameChunk(EcsSnapshotChunk other) =>
        Offset == other.Offset && Count == other.Count && First == other.First;
}
