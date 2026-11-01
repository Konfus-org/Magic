using System.Runtime.InteropServices;

namespace Magic.Ecs;

/// <summary>
/// Changes to which components entities have, recorded during a query's loop or a run, where making them would move
/// entities out from under the loop's spans, and made after it in one go by <c>ecs.Apply(changes)</c>: a command
/// buffer. Recording and applying box nothing, and one kept and reused allocates nothing once it has grown.
/// Not safe across threads: record on the thread that applies.
/// </summary>
public sealed class EntityChanges
{
    private readonly List<Change> _changes = [];

    /// <summary>
    /// The components recorded by <see cref="Set{T}"/>, one column per type, reused apply after apply.
    /// </summary>
    private readonly Dictionary<Type, IColumn> _columns = [];

    public void Destroy(Entity entity) => _changes.Add(new Change(ChangeKind.Destroy, entity, null, 0));

    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent
    {
        Column<T> column = GetColumn<T>();
        _changes.Add(new Change(ChangeKind.Set, entity, column, column.Add(component)));
    }

    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent => _changes.Add(new Change(ChangeKind.Remove, entity, GetColumn<T>(), 0));

    /// <summary>
    /// Makes every change recorded on <paramref name="ecs"/>, in the order recorded, then holds none. A change to an
    /// entity that is no longer alive, destroyed by an earlier change or since it was recorded, is skipped.
    /// </summary>
    internal void Apply(IEcs ecs)
    {
        foreach (Change change in _changes)
        {
            if (!ecs.IsAlive(change.Entity))
                continue;

            if (change.Kind == ChangeKind.Destroy)
                ecs.Destroy(change.Entity);
            else if (change.Kind == ChangeKind.Set)
                change.Column?.Set(ecs, change.Entity, change.Slot);
            else
                change.Column?.Remove(ecs, change.Entity);
        }

        _changes.Clear();
        foreach (IColumn column in _columns.Values)
        {
            column.Clear();
        }
    }

    private Column<T> GetColumn<T>()
        where T : unmanaged, IComponent
    {
        ref IColumn? column = ref CollectionsMarshal.GetValueRefOrAddDefault(_columns, typeof(T), out _);
        column ??= new Column<T>();

        return (Column<T>)column;
    }

    private enum ChangeKind : byte
    {
        Destroy,
        Set,
        Remove,
    }

    /// <summary>
    /// The columns of every component type recorded, as one kind for <see cref="Apply"/>: type erasure over
    /// <see cref="Column{T}"/>.
    /// </summary>
    private interface IColumn
    {
        void Set(IEcs ecs, Entity entity, int slot);

        void Remove(IEcs ecs, Entity entity);

        void Clear();
    }

    /// <summary>
    /// One change: what it does, to which entity, and for a component, its type's column and, for a set, where in the
    /// column its value is.
    /// </summary>
    private readonly record struct Change(ChangeKind Kind, Entity Entity, IColumn? Column, int Slot);

    /// <inheritdoc cref="IColumn"/>
    private sealed class Column<T> : IColumn
        where T : unmanaged, IComponent
    {
        private readonly List<T> _values = [];

        /// <summary>
        /// Keeps <paramref name="component"/> and says where.
        /// </summary>
        public int Add(in T component)
        {
            _values.Add(component);

            return _values.Count - 1;
        }

        public void Set(IEcs ecs, Entity entity, int slot) =>
            ecs.Set(entity, in CollectionsMarshal.AsSpan(_values)[slot]);

        public void Remove(IEcs ecs, Entity entity) => ecs.Remove<T>(entity);

        public void Clear() => _values.Clear();
    }
}
