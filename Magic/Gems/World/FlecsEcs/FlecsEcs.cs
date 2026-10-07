using Flecs.NET.Bindings;
using Flecs.NET.Core;
using Magic.Contexts;
using Magic.Interfaces;
using Magic.Utils;
using System.Runtime.InteropServices;
using FlecsEntity = Flecs.NET.Core.Entity;
using FlecsWorldHandle = Flecs.NET.Core.World;
using Handle = Magic.Contexts.Handle;

namespace FlecsEcsGem;

/// <summary>
/// The pile of entities, stored in a flecs world. Entity handles are flecs ids, so converting between the two
/// costs nothing. Components live in flecs' native columns; nothing about an entity is a managed object. Each
/// frame hook runs the flecs pipeline of its phase, which runs every enabled system scheduled on it (see
/// <see cref="Add(ISystem)"/>). The world is never Progress()ed.
/// </summary>
internal sealed unsafe class FlecsEcs : IGem, IEcs
{
    private readonly FlecsGroup _group;
    private readonly Ecs.EachEntityCallback _addChild;
    private List<Handle>? _children; // where _addChild puts what GetChildren finds
    private readonly Dictionary<string, int> _added = []; // systems added, by type name, to tell two of one type apart
    private Frame _frame; // the frame of the phase running now, which the systems are handed

    public FlecsEcs()
    {
        Native = FlecsWorldHandle.Create();
        Phases = new Phases(Native);
        _group = new FlecsGroup(this);
        _addChild = child => _children?.Add(new Handle(child.Id));
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;
        _group.Dispose(); // a group left open lands before the world goes
        Native.Dispose(); // destroys every entity, query and system with it
    }

    public int EntityCount => Native.Count(Ecs.Any);

    /// <summary>
    /// Phase tags and pipelines, one per <see cref="UpdateType"/>.
    /// </summary>
    internal Phases Phases { get; }

    internal FlecsWorldHandle Native { get; }

    /// <summary>
    /// True once the flecs world is gone; handles created from it become no-ops.
    /// </summary>
    internal bool IsDisposed { get; private set; }

    public void Update(in Frame frame)
    {
        RunPhase(UpdateType.Update, frame);
    }

    public void FixedUpdate(in Frame frame)
    {
        RunPhase(UpdateType.FixedUpdate, frame);
    }

    public void LateUpdate(in Frame frame)
    {
        RunPhase(UpdateType.LateUpdate, frame);
    }

    public void Render(in Frame frame)
    {
        RunPhase(UpdateType.Render, frame);
        RunPhase(UpdateType.Overlay, frame);
    }

    public Handle Create(string? name = null, Handle parent = default)
    {
        // Anonymous first, then parent, then name: naming an entity that already has a parent scopes the
        // name to its siblings, which is what "unique among siblings" means.
        if (_group.IsOpen)
            return _group.Create(name, parent);

        FlecsEntity entity = Native.Entity();
        if (parent.IsValid)
            entity.ChildOf(parent.Id);

        if (name is not null)
            entity.SetName(name);

        return new Handle(entity.Id);
    }

    public IDisposable Group()
    {
        _group.Open();

        return _group;
    }

    public void Destroy(Handle entity)
    {
        ApplyGroupFor(entity);
        ToEntity(entity).Destruct();
    }

    public bool IsAlive(Handle entity)
    {
        return entity.IsValid && Native.IsAlive(entity.Id);
    }

    public void Enable(Handle entity, bool enabled = true)
    {
        FlecsEntity native = ToEntity(entity);
        if (enabled)
            native.Enable();
        else
            native.Disable();
    }

    public bool IsEnabled(Handle entity)
    {
        return ToEntity(entity).Enabled();
    }

    public string? GetName(Handle entity)
    {
        string name = ToEntity(entity).Name();

        return name.Length == 0 ? null : name;
    }

    public void SetName(Handle entity, string? name)
    {
        ApplyGroupFor(entity);
        if (name is null)
            flecs.ecs_set_name(Native.Handle, entity.Id, null);
        else
            ToEntity(entity).SetName(name);
    }

    public Handle Lookup(string path)
    {
        return new Handle(Native.Lookup(path).Id);
    }

    public Handle GetParent(Handle entity)
    {
        return new Handle(ToEntity(entity).Parent().Id);
    }

    public void SetParent(Handle entity, Handle parent)
    {
        ApplyGroupFor(entity);
        FlecsEntity native = ToEntity(entity);
        if (parent.IsValid)
            native.ChildOf(parent.Id);
        else
            native.Remove(Ecs.ChildOf, Ecs.Wildcard);
    }

    public void GetChildren(Handle parent, List<Handle> children)
    {
        _children = children;
        ToEntity(parent).Children(_addChild);
        _children = null;
    }

    public void Set<T>(Handle entity, in T value) where T : unmanaged
    {
        if (_group.IsOpen && _group.TryGive(entity.Id, Type<T>.Id(Native), Layout<T>.IsTag ? default : MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))))
            return;

        ToEntity(entity).Set(value);
    }

    public ref T Get<T>(Handle entity) where T : unmanaged
    {
        // One lookup: the pointer is null when the entity has no T (a tag has no value to point at).
        T* value = Layout<T>.IsTag ? null : ToEntity(entity).GetMutPtr<T>();
        if (value == null)
            throw new InvalidOperationException($"{entity} has no {typeof(T).Name} component.");

        return ref *value;
    }

    public bool TryGet<T>(Handle entity, out T value) where T : unmanaged
    {
        FlecsEntity native = ToEntity(entity);
        if (Layout<T>.IsTag)
        {
            value = default;
            return native.Has<T>();
        }

        // One lookup, not Has then Get: this is on the hot paths of transforms, scripts and visibility.
        T* found = native.GetPtr<T>();
        value = found == null ? default : *found;
        return found != null;
    }

    public bool Has<T>(Handle entity) where T : unmanaged
    {
        return ToEntity(entity).Has<T>();
    }

    public void Add<T>(Handle entity) where T : unmanaged
    {
        // An entity made in an open group has nothing yet: the component is kept for it, at its default.
        T blank = default;
        if (_group.IsOpen && _group.TryGive(entity.Id, Type<T>.Id(Native), Layout<T>.IsTag ? default : MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in blank))))
            return;

        FlecsEntity native = ToEntity(entity);
        if (native.Has<T>())
            return;

        native.Add<T>();

        // flecs leaves a freshly added component's memory as it found it, and a recycled column holds whatever
        // lived there before; the contract is a default value, so a component (not a tag) is cleared here.
        if (!Layout<T>.IsTag)
            native.GetMut<T>() = default;
    }

    public void Remove<T>(Handle entity) where T : unmanaged
    {
        ApplyGroupFor(entity);
        ToEntity(entity).Remove<T>();
    }

    public IEcsQueryBuilder<T1> Query<T1>() where T1 : unmanaged
    {
        return new FlecsQueryBuilder<T1>(this);
    }

    public IEcsQueryBuilder<T1, T2> Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged
    {
        return new FlecsQueryBuilder<T1, T2>(this);
    }

    public Subscription Add(ISystem system)
    {
        // Flecs knows a system by name: the type's, numbered from the second of a type on.
        string name = system.GetType().Name;
        int count = _added[name] = _added.GetValueOrDefault(name) + 1;
        if (count > 1)
            name = $"{name}_{count}";

        // Immediate: on the main thread with the world writable, and with deferring suspended so what the system
        // changes (an entity created, a component added) is there the moment it asks for it, as outside a pipeline.
        System_ flecsSystem = new SystemBuilder(Native.Handle, name)
            .Kind(Phases.Tag(system.Phase))
            .Immediate()
            .Run((Iter _) =>
            {
                Native.DeferSuspend();
                try
                {
                    system.Run(_frame);
                }
                finally
                {
                    Native.DeferResume();
                }
            });

        return FlecsSystem.Subscribe(this, flecsSystem.Entity.Id);
    }

    public Subscription Observe<T>(ComponentEvent e, Action<Handle> handler) where T : unmanaged
    {
        ulong evt = e switch
        {
            ComponentEvent.Added => Ecs.OnAdd,
            ComponentEvent.Set => Ecs.OnSet,
            _ => Ecs.OnRemove,
        };

        Observer observer = Native.Observer()
            .With<T>()
            .Event(evt)
            .Each((FlecsEntity entity) => handler(new Handle(entity.Id)));

        return FlecsSystem.Subscribe(this, observer.Entity.Id);
    }

    /// <summary>
    /// What an open group holds back is applied now when <paramref name="entity"/> is one of the entities it made:
    /// for a change a group does not keep (a removal, a new parent or name, its destruction), which needs the
    /// entity as it stands.
    /// </summary>
    private void ApplyGroupFor(Handle entity)
    {
        if (_group.IsOpen && _group.Holds(entity.Id))
            _group.Apply();
    }

    private void RunPhase(UpdateType phase, in Frame frame)
    {
        if (IsDisposed)
            return;

        _frame = frame;
        Native.RunPipeline(Phases.Pipeline(phase), frame.Delta);
    }

    private FlecsEntity ToEntity(Handle entity)
    {
        return Native.Entity(entity.Id);
    }

    /// <summary>
    /// Whether <typeparamref name="T"/> is a tag (a struct with no fields), which flecs stores no memory for.
    /// </summary>
    private static class Layout<T> where T : unmanaged
    {
        public static readonly bool IsTag = typeof(T)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Length == 0;
    }
}
