using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Magic.Ecs;

/// <summary>
/// Changes to entities, pushed as data for bulk work, and what came of them. Fill a list of your own and run it at once with
/// <c>world.Apply</c>, which is also how to change entities straight after a query's loop, where they can't change during
/// it. Pushing and making box nothing but what is boxed already, and one kept and reused allocates nothing once it has
/// grown. Main thread.
/// </summary>
public sealed class EntityCommands
{
    private readonly List<Change> _changes = [];

    private readonly List<ImmutableArray<IComponent>> _entities = [];

    /// <summary>
    /// The components pushed already boxed, whatever their type.
    /// </summary>
    private readonly List<IComponent> _boxed = [];

    /// <summary>
    /// The components pushed by their type, one column per type or pair of types, reused run after run.
    /// </summary>
    private readonly Dictionary<Type, IBatch> _columns = [];

    private readonly List<Entity> _created = [];

    private readonly List<Entity> _destroyed = [];

    /// <summary>
    /// The entities made, in the order pushed. Good until the list is cleared, which its owner does as its run ends.
    /// </summary>
    public ReadOnlySpan<Entity> Created => CollectionsMarshal.AsSpan(_created);

    /// <summary>
    /// The entities destroyed, no longer alive, in the order pushed. Good until the list is cleared.
    /// </summary>
    public ReadOnlySpan<Entity> Destroyed => CollectionsMarshal.AsSpan(_destroyed);

    /// <summary>
    /// One new entity for each of <paramref name="entities"/>.
    /// </summary>
    public void Create(ReadOnlySpan<ImmutableArray<IComponent>> entities)
    {
        _changes.Add(new Change(ChangeKind.CreateFromComponents, default, null, _entities.Count, entities.Length));
        _entities.AddRange(entities);
    }

    /// <summary>
    /// One new entity per element of <paramref name="first"/>, each with the component at its index.
    /// </summary>
    public void Create<T1>(ReadOnlySpan<T1> first)
        where T1 : unmanaged, IComponent
    {
        Column<T1> column = GetColumn<Column<T1>>();
        _changes.Add(new Change(ChangeKind.Create, default, column, column.Add(first), first.Length));
    }

    /// <summary>
    /// <see cref="Create{T1}"/> with two components each. The spans are the same length.
    /// </summary>
    public void Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        if (second.Length != first.Length)
        {
            throw new ArgumentException(
                $"A batch's spans are the same length: {first.Length} {typeof(T1).Name}, "
                    + $"{second.Length} {typeof(T2).Name}.",
                nameof(second)
            );
        }

        Pair<T1, T2> pair = GetColumn<Pair<T1, T2>>();
        _changes.Add(new Change(ChangeKind.Create, default, pair, pair.Add(first, second), first.Length));
    }

    /// <summary>
    /// Gives <paramref name="entity"/> each of <paramref name="components"/>, or overwrites the one it has.
    /// </summary>
    public void Set(Entity entity, ReadOnlySpan<IComponent> components)
    {
        _changes.Add(new Change(ChangeKind.SetBoxed, entity, null, _boxed.Count, components.Length));
        _boxed.AddRange(components);
    }

    /// <summary>
    /// Gives <paramref name="entity"/> <paramref name="component"/>, or overwrites the one it has.
    /// </summary>
    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent
    {
        Column<T> column = GetColumn<Column<T>>();
        _changes.Add(new Change(ChangeKind.Set, entity, column, column.Add(new ReadOnlySpan<T>(in component)), 1));
    }

    /// <summary>
    /// Gives each of <paramref name="entities"/> the component at its index. The spans are the same length.
    /// </summary>
    public void Set<T>(ReadOnlySpan<Entity> entities, ReadOnlySpan<T> components)
        where T : unmanaged, IComponent
    {
        if (components.Length != entities.Length)
        {
            throw new ArgumentException(
                $"A batch's spans are the same length: {entities.Length} entities, {components.Length} "
                    + $"{typeof(T).Name}.",
                nameof(components)
            );
        }

        for (int i = 0; i < entities.Length; i++)
        {
            Set(entities[i], in components[i]);
        }
    }

    /// <summary>
    /// Takes <typeparamref name="T"/> off <paramref name="entity"/>; nothing happens when it has none.
    /// </summary>
    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent =>
        _changes.Add(new Change(ChangeKind.Remove, entity, GetColumn<Column<T>>(), 0, 0));

    public void Destroy(ReadOnlySpan<Entity> entities)
    {
        foreach (Entity entity in entities)
        {
            _changes.Add(new Change(ChangeKind.Destroy, entity, null, 0, 0));
        }
    }

    /// <summary>
    /// Makes every change pushed on <paramref name="ecs"/>, in the order pushed, and fills in <see cref="Created"/> and
    /// <see cref="Destroyed"/>. A change to an entity that is no longer alive, destroyed by an earlier change or since it was
    /// pushed, is skipped.
    /// </summary>
    public void Apply(IEcs ecs)
    {
        ArgumentNullException.ThrowIfNull(ecs);
        foreach (Change change in _changes)
        {
            if (change.Kind == ChangeKind.CreateFromComponents)
                CreateFromComponents(ecs, change);
            else if (change.Kind == ChangeKind.Create)
                change.Batch?.Create(ecs, change.Slot, change.Count, _created);
            else if (ecs.IsAlive(change.Entity))
                ApplyToEntity(ecs, change);
        }

        _changes.Clear();
    }

    /// <summary>
    /// Forgets everything, pushed or done, for its next use.
    /// </summary>
    public void Clear()
    {
        _changes.Clear();
        _entities.Clear();
        _boxed.Clear();
        _created.Clear();
        _destroyed.Clear();
        foreach (IBatch column in _columns.Values)
        {
            column.Clear();
        }
    }

    /// <summary>
    /// <paramref name="count"/> more elements on the end of <paramref name="created"/>, to be written.
    /// </summary>
    private static Span<Entity> Grow(List<Entity> created, int count)
    {
        int before = created.Count;
        CollectionsMarshal.SetCount(created, before + count);

        return CollectionsMarshal.AsSpan(created)[before..];
    }

    private void CreateFromComponents(IEcs ecs, Change change)
    {
        for (int i = change.Slot; i < change.Slot + change.Count; i++)
        {
            _created.Add(ecs.Create(_entities[i].AsSpan()));
        }
    }

    private void ApplyToEntity(IEcs ecs, Change change)
    {
        switch (change.Kind)
        {
            case ChangeKind.Destroy:
                ecs.Destroy(change.Entity);
                _destroyed.Add(change.Entity);
                break;
            case ChangeKind.SetBoxed:
                ecs.Set(change.Entity, CollectionsMarshal.AsSpan(_boxed).Slice(change.Slot, change.Count));
                break;
            case ChangeKind.Set:
                (change.Batch as IColumn)?.Set(ecs, change.Entity, change.Slot);
                break;
            case ChangeKind.Remove:
                (change.Batch as IColumn)?.Remove(ecs, change.Entity);
                break;
            case ChangeKind.CreateFromComponents:
            case ChangeKind.Create:
            default:
                throw new ArgumentException($"{change.Kind} is no change to an entity.", nameof(change));
        }
    }

    private TColumn GetColumn<TColumn>()
        where TColumn : IBatch, new()
    {
        ref IBatch? column = ref CollectionsMarshal.GetValueRefOrAddDefault(_columns, typeof(TColumn), out _);
        column ??= new TColumn();

        return (TColumn)column;
    }

    private enum ChangeKind : byte
    {
        CreateFromComponents,
        Create,
        Destroy,
        SetBoxed,
        Set,
        Remove,
    }

    /// <summary>
    /// What makes entities from the components pushed by their types, as one kind for <see cref="Apply"/>: type erasure over
    /// <see cref="Column{T}"/> and <see cref="Pair{T1, T2}"/>.
    /// </summary>
    private interface IBatch
    {
        void Create(IEcs ecs, int slot, int count, List<Entity> created);

        void Clear();
    }

    /// <summary>
    /// <see cref="IBatch"/> over one component type, which also changes an entity's.
    /// </summary>
    private interface IColumn : IBatch
    {
        void Set(IEcs ecs, Entity entity, int slot);

        void Remove(IEcs ecs, Entity entity);
    }

    /// <summary>
    /// One change: what it does, to which entity, its components' column, and where in that or the list it reads from its
    /// values start and how many there are.
    /// </summary>
    private readonly record struct Change(ChangeKind Kind, Entity Entity, IBatch? Batch, int Slot, int Count);

    /// <inheritdoc cref="IColumn"/>
    private sealed class Column<T> : IColumn
        where T : unmanaged, IComponent
    {
        private readonly List<T> _values = [];

        /// <summary>
        /// Keeps <paramref name="components"/> and says where they start.
        /// </summary>
        public int Add(ReadOnlySpan<T> components)
        {
            int slot = _values.Count;
            _values.AddRange(components);

            return slot;
        }

        public void Create(IEcs ecs, int slot, int count, List<Entity> created) =>
            ecs.Create(CollectionsMarshal.AsSpan(_values).Slice(slot, count), Grow(created, count));

        public void Set(IEcs ecs, Entity entity, int slot) =>
            ecs.Set(entity, in CollectionsMarshal.AsSpan(_values)[slot]);

        public void Remove(IEcs ecs, Entity entity) => ecs.Remove<T>(entity);

        public void Clear() => _values.Clear();
    }

    /// <inheritdoc cref="IBatch"/>
    private sealed class Pair<T1, T2> : IBatch
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        private readonly List<T1> _first = [];
        private readonly List<T2> _second = [];

        /// <summary>
        /// Keeps <paramref name="first"/> and <paramref name="second"/>, the same length, and says where they start.
        /// </summary>
        public int Add(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second)
        {
            int slot = _first.Count;
            _first.AddRange(first);
            _second.AddRange(second);

            return slot;
        }

        public void Create(IEcs ecs, int slot, int count, List<Entity> created) =>
            ecs.Create(
                CollectionsMarshal.AsSpan(_first).Slice(slot, count),
                CollectionsMarshal.AsSpan(_second).Slice(slot, count),
                Grow(created, count)
            );

        public void Clear()
        {
            _first.Clear();
            _second.Clear();
        }
    }
}
