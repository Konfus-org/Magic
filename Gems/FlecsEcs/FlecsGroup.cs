using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Handle = Magic.Contexts.Handle;

namespace FlecsGem;

/// <summary>
/// The entities made while a group is open (<see cref="Magic.Interfaces.IEcs.Group"/>), with what they were given,
/// held back until it ends. Flecs keeps entities with the same components in one table, so an entity given its
/// components one call at a time is moved from table to table, once per call; held back, the ones that end up with
/// the same parent and the same components go into their table together, in one insert, their values copied a
/// column at a time. Only entities made in the group are held back: a change to any other entity happens at once,
/// as outside a group. Main thread.
/// </summary>
internal sealed unsafe class FlecsGroup(FlecsEcs ecs) : IDisposable
{
    /// <summary>
    /// The most components (its parent included) flecs inserts an entity with in one go; one with more is given them one by one.
    /// </summary>
    private const int MaxIds = 32;

    private readonly List<Made> _made = [];
    private readonly Dictionary<ulong, int> _indexById = [];
    private readonly List<Given> _given = [];
    private readonly Dictionary<ulong, List<int>> _bySignature = [];
    private readonly Stack<List<int>> _spareLists = new();
    private byte[] _values = new byte[64 * 1024];
    private byte[] _column = new byte[64 * 1024];
    private ulong[] _ids = new ulong[1024];
    private int _valuesUsed;
    private int _depth;
    private ulong _lastId; // most calls are about the entity made last
    private int _lastIndex;

    /// <summary>
    /// Ends the group; the outermost one applies everything held back.
    /// </summary>
    public void Dispose()
    {
        if (_depth == 0 || --_depth > 0)
            return;

        Apply();
    }

    public bool IsOpen => _depth > 0;

    public void Open()
    {
        _depth++;
    }

    /// <summary>
    /// Makes an entity that exists, so its handle is good, but has nothing yet.
    /// </summary>
    public Handle Create(string? name, Handle parent)
    {
        ulong id = flecs.ecs_new(ecs.Native.Handle);
        _lastId = id;
        _lastIndex = _made.Count;
        _indexById[id] = _lastIndex;
        _made.Add(new Made(id, parent.Id, name, First: -1, Last: -1, Count: 0, Signature: parent.Id * 0x9E3779B97F4A7C15ul));

        return new Handle(id);
    }

    /// <summary>
    /// Whether the entity was made in this group and is still held back.
    /// </summary>
    public bool Holds(ulong entity)
    {
        return entity == _lastId || _indexById.ContainsKey(entity);
    }

    /// <summary>
    /// Keeps a component's value (none for a tag) for an entity made in this group; false for any other entity. A
    /// component given twice keeps the later value.
    /// </summary>
    public bool TryGive(ulong entity, ulong component, ReadOnlySpan<byte> value)
    {
        int index;
        if (entity == _lastId)
            index = _lastIndex;
        else if (!_indexById.TryGetValue(entity, out index))
            return false;

        Made made = _made[index];
        for (int i = made.First; i >= 0; i = _given[i].Next)
        {
            if (_given[i].Component != component)
                continue;

            value.CopyTo(_values.AsSpan(_given[i].Offset, _given[i].Size));
            return true;
        }

        if (_valuesUsed + value.Length > _values.Length)
            Array.Resize(ref _values, Math.Max(_values.Length * 2, _valuesUsed + value.Length));

        value.CopyTo(_values.AsSpan(_valuesUsed));
        int given = _given.Count;
        _given.Add(new Given(component, _valuesUsed, value.Length, Next: -1));
        _valuesUsed += value.Length;

        if (made.Last >= 0)
            _given[made.Last] = _given[made.Last] with { Next = given };

        _made[index] = made with
        {
            First = made.First < 0 ? given : made.First,
            Last = given,
            Count = made.Count + 1,
            Signature = (made.Signature * 31) + component,
        };

        return true;
    }

    /// <summary>
    /// Gives every entity held back what it was given: the ones alike (same parent, same components in the same
    /// order) in one insert each. Then they are entities like any other, and the group goes on empty.
    /// </summary>
    public void Apply()
    {
        if (_made.Count == 0)
            return;

        for (int i = 0; i < _made.Count; i++)
        {
            if (!_bySignature.TryGetValue(_made[i].Signature, out List<int>? alike))
                _bySignature[_made[i].Signature] = alike = _spareLists.Count > 0 ? _spareLists.Pop() : [];

            alike.Add(i);
        }

        foreach (List<int> alike in _bySignature.Values)
        {
            Insert(alike);
            alike.Clear();
            _spareLists.Push(alike);
        }

        // Named last: a name is unique among siblings, so the parent has to be there first.
        foreach (Made made in _made)
        {
            if (made.Name is not null)
                ecs.Native.Entity(made.Id).SetName(made.Name);
        }

        _bySignature.Clear();
        _made.Clear();
        _indexById.Clear();
        _given.Clear();
        _valuesUsed = 0;
        _lastId = 0;
    }

    /// <summary>
    /// One insert for the entities of <paramref name="alike"/>, which have the same signature. Two different sets of
    /// components could share one by chance, and flecs takes only so many in one go: an entity that is not like the
    /// first, or has too many, is given its components one by one instead.
    /// </summary>
    private void Insert(List<int> alike)
    {
        Made first = _made[alike[0]];
        int idCount = first.Count + (first.Parent != 0 ? 1 : 0);
        if (idCount == 0)
            return; // made and given nothing: it is there already

        if (_ids.Length < alike.Count)
            _ids = new ulong[Math.Max(alike.Count, _ids.Length * 2)];

        int count = 0;
        foreach (int index in alike)
        {
            if (idCount <= MaxIds && IsLike(first, _made[index]))
                _ids[count++] = _made[index].Id;
            else
                GiveOneByOne(_made[index]);
        }

        if (count == 0)
            return;

        // Every column's values side by side in one buffer, in the order the entities are inserted.
        int rowBytes = 0;
        for (int i = first.First; i >= 0; i = _given[i].Next)
            rowBytes += _given[i].Size;

        if (_column.Length < rowBytes * count)
            _column = new byte[Math.Max(rowBytes * count, _column.Length * 2)];

        flecs.ecs_bulk_desc_t desc = default;
        void** data = stackalloc void*[MaxIds];
        fixed (byte* column = _column)
        fixed (byte* values = _values)
        fixed (ulong* ids = _ids)
        {
            int id = 0, columnStart = 0;
            for (int i = first.First; i >= 0; i = _given[i].Next, id++)
            {
                int size = _given[i].Size;
                desc.ids[id] = _given[i].Component;
                data[id] = size == 0 ? null : column + columnStart;
                if (size == 0)
                    continue;

                // The n-th component of every entity: they were given in the same order, so the chains line up.
                int row = 0;
                foreach (int index in alike)
                {
                    Made made = _made[index];
                    if (!IsLike(first, made))
                        continue;

                    int given = made.First;
                    for (int step = 0; step < id; step++)
                        given = _given[given].Next;

                    Buffer.MemoryCopy(values + _given[given].Offset, column + columnStart + (row * size), size, size);
                    row++;
                }

                columnStart += size * count;
            }

            if (first.Parent != 0)
            {
                desc.ids[id] = Ecs.Pair(Ecs.ChildOf, first.Parent);
                data[id] = null;
            }

            desc.entities = ids;
            desc.count = count;
            desc.data = data;
            flecs.ecs_bulk_init(ecs.Native.Handle, &desc);
        }
    }

    /// <summary>
    /// The same parent and the same components in the same order.
    /// </summary>
    private bool IsLike(in Made first, in Made other)
    {
        if (first.Id == other.Id)
            return true;
        if (first.Parent != other.Parent || first.Count != other.Count)
            return false;

        for (int a = first.First, b = other.First; a >= 0; a = _given[a].Next, b = _given[b].Next)
        {
            if (_given[a].Component != _given[b].Component)
                return false;
        }

        return true;
    }

    private void GiveOneByOne(in Made made)
    {
        flecs.ecs_world_t* world = ecs.Native.Handle;
        if (made.Parent != 0)
            flecs.ecs_add_id(world, made.Id, Ecs.Pair(Ecs.ChildOf, made.Parent));

        fixed (byte* values = _values)
        {
            for (int i = made.First; i >= 0; i = _given[i].Next)
            {
                if (_given[i].Size == 0)
                    flecs.ecs_add_id(world, made.Id, _given[i].Component);
                else
                    flecs.ecs_set_id(world, made.Id, _given[i].Component, _given[i].Size, values + _given[i].Offset);
            }
        }
    }

    /// <summary>
    /// An entity made in the group: its parent, its name, and the chain of what it was given, with a hash of all of it.
    /// </summary>
    private readonly record struct Made(ulong Id, ulong Parent, string? Name, int First, int Last, int Count, ulong Signature);

    /// <summary>
    /// One component an entity was given: where its value is kept, and the next one of the same entity.
    /// </summary>
    private readonly record struct Given(ulong Component, int Offset, int Size, int Next);
}
