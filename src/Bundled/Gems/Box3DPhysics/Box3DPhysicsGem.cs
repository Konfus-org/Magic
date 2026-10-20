using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Queries;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Box3DPhysics;

/// <summary>
/// Moves entities as rigid bodies with Box3D. Every entity with a <see cref="Transform"/> and a <see cref="Physics"/>
/// gets a body shaped as the component says and sized to its model (<see cref="ModelShapes"/>); the body moves the
/// transform, and the components move the body: once per fixed step, after the scripts' own, each entity's components
/// are compared with the copy kept from the last step, so a transform a script moved moves the body there (a kinematic
/// body is driven to it, pushing what is in the way), a velocity it set is applied, and a change to anything that
/// shapes the body remakes it. Then the world steps, and every body that moved writes its position, rotation and
/// velocities back. A body whose entity or component is gone is destroyed at the next step, as is one that has gone
/// farther from every player camera than the settings' range, so a world full of bodies is simulated only around the
/// player; an entity past the range waits where it is, body or no body, until the player comes back. The models a
/// domain's bodies are shaped like are fitted before it spawns (<see cref="IPreloader"/>), so its bodies are there from
/// its first step.
/// <para>
/// It answers <see cref="IPhysics"/> from the bodies as the last step left them, and after each step writes what
/// every body that listens, one whose entity has a <see cref="Contacts"/>, is touching, with the phase worked out
/// against what the component held the step before.
/// </para>
/// <para>
/// Box3D works in metres, kilograms and seconds with +Y up, as the engine does.
/// </para>
/// </summary>
internal sealed unsafe class Box3DPhysicsGem : IGem, IPhysics, IPreloader, IDisposable
{
    private readonly World _world;
    private readonly Threads _threads;
    private readonly Box3DPhysicsSettings _settings;
    private readonly ModelShapes _shapes;
    private readonly List<Body> _bodies = [];
    private readonly Dictionary<Entity, int> _slots = [];
    private readonly Stack<int> _free = [];
    private readonly List<Entity> _gone = [];
    private readonly List<Vector3> _cameras = [];

    /// <summary>
    /// The domains whose models are being fitted before they spawn, each checked every step until all are
    /// (<see cref="CheckPreloads"/>). Main thread.
    /// </summary>
    private readonly List<Preload> _preloads = [];
    private B3.WorldId _box3d;
    private float _gravity;

    /// <summary>
    /// How much farther than the range a body goes before it is dropped, so one at the edge is not made and dropped
    /// every other step.
    /// </summary>
    private const float Leave = 1.25f;

    /// <summary>
    /// The most touching contacts read for one body, which bounds the stack they are read onto; a body touching more
    /// hears the first of them.
    /// </summary>
    private const int MostTouching = 64;

    /// <summary>
    /// Which step this is, stamped on each body seen in it: a body not stamped is of an entity that is gone, or out
    /// of range.
    /// </summary>
    private uint _step;

    public Box3DPhysicsGem(World world, Assets assets, Threads threads, Box3DPhysicsSettings settings)
    {
        _world = world;
        _threads = threads;
        _settings = settings;
        _shapes = new ModelShapes(assets, threads);
        B3.SetAssertFcn(&OnAssert);
        B3.SetLogFcn(&OnLog);
        B3.WorldDef def = B3.DefaultWorldDef();
        _gravity = settings.Gravity;
        def.Gravity = new B3.Vec3 { Y = -_gravity };
        def.WorkerCount = (uint)Math.Clamp(settings.Workers, 1, B3.MaxWorkers);
        _box3d = B3.CreateWorld(&def);
        B3.Version version = B3.GetVersion();
        Debugging.Info($"Box3D {version.Major}.{version.Minor}.{version.Revision} is simulating, on {def.WorkerCount} thread(s).");
    }

    /// <summary>
    /// Destroys the world, and every body with it, then the shapes the bodies shared.
    /// </summary>
    public void Dispose()
    {
        B3.DestroyWorld(_box3d);
        _shapes.Dispose();
    }

    public void FixedUpdate(in Frame frame)
    {
        _step++;
        CheckPreloads();
        if (_settings.Gravity != _gravity)
        {
            _gravity = _settings.Gravity;
            B3.WorldSetGravity(_box3d, new B3.Vec3 { Y = -_gravity });
        }

        _cameras.Clear();
        foreach ((_, _, Span<Transform> transforms, Span<Tags> tags) in _world.Query<Camera, Transform, Tags>())
        {
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i].Has(Tag.Player))
                {
                    _cameras.Add(transforms[i].Position);
                }
            }
        }

        foreach (EcsChunk<Transform, Physics> chunk in _world.Query<Transform, Physics>())
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Physics> physics) = chunk;
            for (int i = 0; i < entities.Length; i++)
            {
                Follow(entities[i], ref transforms[i], ref physics[i], frame.DeltaSeconds);
            }
        }

        foreach ((Entity entity, int slot) in _slots)
        {
            if (_bodies[slot].Seen != _step)
            {
                _gone.Add(entity);
            }
        }

        foreach (Entity entity in _gone)
        {
            Destroy(entity);
        }

        if (_gone.Count > 0)
        {
            Debugging.Verbose($"{_gone.Count} bodies dropped: their entities gone, or out of range of the player.");
        }

        _gone.Clear();
        B3.WorldStep(_box3d, frame.DeltaSeconds, _settings.Substeps);
        B3.BodyEvents events = B3.WorldGetBodyEvents(_box3d);
        for (int i = 0; i < events.MoveCount; i++)
        {
            Moved(events.MoveEvents[i]);
        }

        foreach ((ReadOnlySpan<Entity> entities, Span<Contacts> contacts) in _world.Query<Contacts>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                Touch(_slots.TryGetValue(entities[i], out int slot) ? slot : -1, ref contacts[i]);
            }
        }
    }

    public RayHit? Cast(Vector3 from, Vector3 to, Motions motions = Motions.All)
    {
        B3.RayResult result = B3.WorldCastRayClosest(_box3d, ToVec(from), ToVec(to - from), FilterOf(motions));
        if (!result.Hit)
        {
            return null;
        }

        Entity entity = _bodies[SlotOf(result.ShapeId)].Entity;
        return new RayHit(entity, FromVec(result.Point), FromVec(result.Normal), result.Fraction * Vector3.Distance(from, to));
    }

    public int Overlap(Vector3 center, float radius, Span<Entity> found, Motions motions = Motions.All)
    {
        if (found.IsEmpty)
        {
            return 0;
        }

        B3.Vec3 point = default;
        B3.ShapeProxy proxy = new() { Points = &point, Count = 1, Radius = radius };
        OverlapQuery query = new() { Capacity = found.Length };
        fixed (Entity* into = found)
        fixed (Body* bodies = CollectionsMarshal.AsSpan(_bodies))
        {
            query.Found = into;
            query.Bodies = bodies;
            B3.WorldOverlapShape(_box3d, ToVec(center), &proxy, FilterOf(motions), (delegate* unmanaged<B3.ShapeId, void*, bool>)(delegate* unmanaged<B3.ShapeId, void*, byte>)&OnOverlap, &query);
        }

        return query.Count;
    }

    /// <summary>
    /// Fits the shape of every model a body among <paramref name="prefabs"/> is shaped like, before they spawn, so
    /// each gets its body in its first step rather than waiting for its model. Done once every shape is there; the
    /// shapes are kept for the run, so what it hands back holds nothing.
    /// </summary>
    public async Task<Subscription> PreloadAsync(IReadOnlyList<Prefab> prefabs, IProgress<float> progress, CancellationToken cancel)
    {
        HashSet<Handle<Model>> models = await Task.Run(() => ModelsOf(prefabs), cancel).ConfigureAwait(false);
        Preload preload = new(models, progress);
        using CancellationTokenRegistration cancelling = cancel.Register(() => _threads.Post(EngineThread.Main, () =>
        {
            if (_preloads.Remove(preload))
            {
                preload.Done.TrySetCanceled(cancel);
            }
        }));
        await _threads.InvokeAsync(EngineThread.Main, () =>
        {
            foreach (Handle<Model> model in models)
            {
                _shapes.Get(model);
            }

            _preloads.Add(preload);
        }, cancel).ConfigureAwait(false);
        await preload.Done.Task.ConfigureAwait(false);
        return new Subscription(static () => { });
    }

    /// <summary>
    /// Makes the body for a new entity in range of a player camera, or carries what its components changed since the
    /// last step over to its body; one out of range is left unstamped, for the sweep to drop.
    /// </summary>
    private void Follow(Entity entity, ref Transform transform, ref Physics physics, float step)
    {
        Handle<Model> model = ModelOf(entity);
        bool hasBody = _slots.TryGetValue(entity, out int slot);
        if (!IsNear(transform, model, hasBody ? _settings.Range * Leave : _settings.Range))
        {
            return;
        }

        if (!hasBody)
        {
            Create(entity, model, transform, physics);
            return;
        }

        ref Body body = ref CollectionsMarshal.AsSpan(_bodies)[slot];
        body.Seen = _step;
        if (IsRemade(body, model, transform, physics))
        {
            Destroy(entity);
            Create(entity, model, transform, physics);
            return;
        }

        bool isMoved = transform.Position != body.Transform.Position || transform.Rotation != body.Transform.Rotation;
        if (physics.Motion == Motion.Kinematic)
        {
            B3.Transform target = new() { P = ToVec(transform.Position), Q = ToQuat(transform.Rotation) };
            B3.BodySetTargetTransform(body.Id, target, step, isMoved);
        }
        else if (isMoved)
        {
            B3.BodySetTransform(body.Id, ToVec(transform.Position), ToQuat(transform.Rotation));
            B3.BodySetAwake(body.Id, true);
        }

        if (physics.Velocity != body.Physics.Velocity)
        {
            B3.BodySetLinearVelocity(body.Id, ToVec(physics.Velocity));
            B3.BodySetAwake(body.Id, true);
        }

        if (physics.AngularVelocity != body.Physics.AngularVelocity)
        {
            B3.BodySetAngularVelocity(body.Id, ToVec(physics.AngularVelocity));
            B3.BodySetAwake(body.Id, true);
        }

        body.Transform = transform;
        body.Physics = physics;
    }

    /// <summary>
    /// Whether the entity's shape reaches within <paramref name="range"/> of a player camera: its centre's distance
    /// less how far the shape reaches from it, so the ground is near from anywhere on it. With no player camera,
    /// everything is near.
    /// </summary>
    private bool IsNear(in Transform transform, Handle<Model> model, float range)
    {
        if (_cameras.Count == 0)
        {
            return true;
        }

        Vector3 scale = Vector3.Abs(transform.Scale);
        ModelShape? shape = _shapes.Get(model);
        float reach = shape is null ? scale.Length() : (((shape.Max - shape.Min) * scale).Length() + ((shape.Max + shape.Min) * scale).Length()) * 0.5f;
        foreach (Vector3 camera in _cameras)
        {
            if (Vector3.Distance(transform.Position, camera) - reach < range)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the components changed in a way that shapes the body, which is then made again.
    /// </summary>
    private static bool IsRemade(in Body body, Handle<Model> model, in Transform transform, in Physics physics)
    {
        return model != body.Model
            || transform.Scale != body.Transform.Scale
            || physics.Motion != body.Physics.Motion
            || physics.Shape != body.Physics.Shape
            || physics.Surface != body.Physics.Surface
            || physics.Bounce != body.Physics.Bounce
            || physics.Mass != body.Physics.Mass;
    }

    /// <summary>
    /// Makes the entity's body and shape, once its model's shape is there; until then the entity waits, unmoved.
    /// </summary>
    private void Create(Entity entity, Handle<Model> model, in Transform transform, in Physics physics)
    {
        ModelShape? shape = _shapes.Get(model);
        if (shape is null)
        {
            return;
        }

        int slot;
        if (_free.Count > 0)
        {
            slot = _free.Pop();
        }
        else
        {
            slot = _bodies.Count;
            _bodies.Add(default);
        }

        B3.BodyDef def = B3.DefaultBodyDef();
        def.Type = physics.Motion switch
        {
            Motion.Dynamic => B3.BodyType.DynamicBody,
            Motion.Kinematic => B3.BodyType.KinematicBody,
            Motion.Static => B3.BodyType.StaticBody,
            _ => B3.BodyType.StaticBody,
        };
        def.Position = ToVec(transform.Position);
        def.Rotation = ToQuat(transform.Rotation);
        def.LinearVelocity = ToVec(physics.Velocity);
        def.AngularVelocity = ToVec(physics.AngularVelocity);
        def.UserData = (void*)(nuint)(slot + 1);
        B3.BodyId id = B3.CreateBody(_box3d, &def);
        B3.ShapeDef shapeDef = B3.DefaultShapeDef();
        shapeDef.BaseMaterial = Surfaces.Material(physics.Surface, physics.Bounce);
        shapeDef.Filter.CategoryBits = (ulong)MotionsOf(physics.Motion);
        Shape kind = physics.Shape;
        if (kind == Shape.Mesh && (physics.Motion != Motion.Static || shape.Mesh is null))
        {
            Debugging.Error(physics.Motion != Motion.Static
                ? $"Entity {entity.Id}'s Physics asks for a mesh shape on a body that moves, which only a static body can have, so it is a box."
                : $"Entity {entity.Id}'s Physics asks for a mesh shape, but its model has no triangles, so it is a box.");
            kind = Shape.Box;
        }

        AddShape(id, &shapeDef, shape, kind, transform.Scale);
        if (physics.Mass > 0 && physics.Motion == Motion.Dynamic)
        {
            B3.MassData mass = B3.BodyGetMassData(id);
            if (mass.Mass > 0)
            {
                float scale = physics.Mass / mass.Mass;
                mass.Mass = physics.Mass;
                mass.Inertia.Cx = Scaled(mass.Inertia.Cx, scale);
                mass.Inertia.Cy = Scaled(mass.Inertia.Cy, scale);
                mass.Inertia.Cz = Scaled(mass.Inertia.Cz, scale);
                B3.BodySetMassData(id, mass);
            }
        }

        _bodies[slot] = new Body { Id = id, Entity = entity, Model = model, Transform = transform, Physics = physics, Seen = _step };
        _slots.Add(entity, slot);
        Debugging.Verbose($"Entity {entity.Id} has a {physics.Motion} {kind} body, {B3.BodyGetMass(id):0.###} kg, at ({transform.Position.X:0.###}, {transform.Position.Y:0.###}, {transform.Position.Z:0.###}).");
    }

    /// <summary>
    /// Adds the shape to the body: the model's bounds at the entity's scale as a box, a sphere or a capsule, or its
    /// triangles at that scale. A bound with no size, a plane's thickness, is given a little, as a shape needs some.
    /// </summary>
    private static void AddShape(B3.BodyId body, B3.ShapeDef* def, ModelShape shape, Shape kind, Vector3 scale)
    {
        const float Least = 0.01f;
        scale = Vector3.Abs(scale);
        Vector3 half = Vector3.Max((shape.Max - shape.Min) * scale * 0.5f, new Vector3(Least));
        Vector3 centre = (shape.Max + shape.Min) * scale * 0.5f;
        switch (kind)
        {
            case Shape.Sphere:
                B3.Sphere sphere = new() { Center = ToVec(centre), Radius = Math.Max(half.X, Math.Max(half.Y, half.Z)) };
                B3.CreateSphereShape(body, def, &sphere);
                break;
            case Shape.Capsule:
                float radius = Math.Max(half.X, half.Z);
                float reach = Math.Max(half.Y - radius, 0);
                B3.Capsule capsule = new()
                {
                    Center1 = ToVec(centre - new Vector3(0, reach, 0)),
                    Center2 = ToVec(centre + new Vector3(0, reach, 0)),
                    Radius = radius,
                };
                B3.CreateCapsuleShape(body, def, &capsule);
                break;
            case Shape.Mesh:
                B3.CreateMeshShape(body, def, shape.Mesh, ToVec(scale));
                break;
            case Shape.Box:
            default:
                B3.BoxHull box = B3.MakeOffsetBoxHull(half.X, half.Y, half.Z, ToVec(centre));
                B3.CreateHullShape(body, def, &box.Base);
                break;
        }
    }

    private void Destroy(Entity entity)
    {
        if (!_slots.Remove(entity, out int slot))
        {
            return;
        }

        B3.DestroyBody(_bodies[slot].Id);
        _bodies[slot] = default;
        _free.Push(slot);
    }

    /// <summary>
    /// Writes where a body is and how it moves back to its entity's components, and keeps the copy the next step
    /// compares them with.
    /// </summary>
    private void Moved(in B3.BodyMoveEvent moved)
    {
        int slot = (int)(nuint)moved.UserData - 1;
        ref Body body = ref CollectionsMarshal.AsSpan(_bodies)[slot];
        if (!_world.IsAlive(body.Entity))
        {
            return;
        }

        ref Transform transform = ref _world.Get<Transform>(body.Entity);
        transform.Position = FromVec(moved.Transform.P);
        transform.Rotation = FromQuat(moved.Transform.Q);
        ref Physics physics = ref _world.Get<Physics>(body.Entity);
        physics.Velocity = FromVec(B3.BodyGetLinearVelocity(body.Id));
        physics.AngularVelocity = FromVec(B3.BodyGetAngularVelocity(body.Id));
        body.Transform = transform;
        body.Physics = physics;
        if (moved.FellAsleep)
        {
            Debugging.Verbose($"Entity {body.Entity.Id}'s body came to rest at ({transform.Position.X:0.###}, {transform.Position.Y:0.###}, {transform.Position.Z:0.###}).");
        }
    }

    /// <summary>
    /// Writes what the body in <paramref name="slot"/> touches after this step over its entity's contacts: each body
    /// it touches now, Stay when the component already held it, else Enter; then each the component held and it no
    /// longer touches, as Exit for this step. An entity with no body, out of range, parts from them all.
    /// </summary>
    private void Touch(int slot, ref Contacts contacts)
    {
        Contacts now = default;
        int written = 0;
        if (slot >= 0)
        {
            Body body = _bodies[slot];
            int capacity = Math.Min(B3.BodyGetContactCapacity(body.Id), MostTouching);
            B3.ContactData* data = stackalloc B3.ContactData[capacity];
            int count = B3.BodyGetContactData(body.Id, data, capacity);
            for (int i = 0; i < count && written < Contacts.Capacity; i++)
            {
                Contact contact = ContactOf(slot, data[i]);
                if (contact.Phase == ContactPhase.None)
                {
                    continue;
                }

                contact.Phase = PhaseOf(contacts, contact.Other) is ContactPhase.Enter or ContactPhase.Stay ? ContactPhase.Stay : ContactPhase.Enter;
                if (contact.Phase == ContactPhase.Enter)
                {
                    Debugging.Verbose($"Entity {body.Entity.Id} touched entity {contact.Other.Id} at ({contact.Point.X:0.###}, {contact.Point.Y:0.###}, {contact.Point.Z:0.###}), {contact.Impulse:0.###} Ns.");
                }

                now[written++] = contact;
            }
        }

        foreach (Contact held in contacts)
        {
            if (held.Phase is ContactPhase.None or ContactPhase.Exit || PhaseOf(now, held.Other) != ContactPhase.None)
            {
                continue;
            }

            if (written == Contacts.Capacity)
            {
                break;
            }

            now[written++] = held with { Phase = ContactPhase.Exit, Impulse = 0 };
            if (slot >= 0)
            {
                Debugging.Verbose($"Entity {_bodies[slot].Entity.Id} parted from entity {held.Other.Id}.");
            }
        }

        contacts = now;
    }

    /// <summary>
    /// One of Box3D's touching contacts as the body in <paramref name="slot"/> sees it: the other body's entity, the
    /// mean of the contact points, the normal turned to point away from the other body, and the impulse along it;
    /// a contact with no points, the two only near, is <see cref="ContactPhase.None"/>.
    /// </summary>
    private Contact ContactOf(int slot, in B3.ContactData data)
    {
        bool isA = SlotOf(data.ShapeIdA) == slot;
        int other = isA ? SlotOf(data.ShapeIdB) : SlotOf(data.ShapeIdA);
        Vector3 centre = FromVec(B3.BodyGetWorldCenterOfMass(B3.ShapeGetBody(data.ShapeIdA)));
        Vector3 point = Vector3.Zero;
        Vector3 normal = Vector3.Zero;
        float impulse = 0;
        int points = 0;
        for (int i = 0; i < data.ManifoldCount; i++)
        {
            ref B3.Manifold manifold = ref data.Manifolds[i];
            for (int j = 0; j < manifold.PointCount; j++)
            {
                point += centre + FromVec(manifold.Points[j].AnchorA);
                impulse += manifold.Points[j].TotalNormalImpulse;
                normal += FromVec(manifold.Normal);
                points++;
            }
        }

        if (points == 0)
        {
            return default;
        }

        normal = Vector3.Normalize(normal);
        return new Contact
        {
            Other = _bodies[other].Entity,
            Point = point / points,
            Normal = isA ? -normal : normal,
            Impulse = impulse,
            Phase = ContactPhase.Stay,
        };
    }

    private static ContactPhase PhaseOf(in Contacts contacts, Entity other)
    {
        foreach (Contact held in contacts)
        {
            if (held.Phase != ContactPhase.None && held.Other == other)
            {
                return held.Phase;
            }
        }

        return ContactPhase.None;
    }

    /// <summary>
    /// The body slot a shape belongs to, kept one up in its body's user data so that 0 is no body.
    /// </summary>
    private static int SlotOf(B3.ShapeId shape) => (int)(nuint)B3.BodyGetUserData(B3.ShapeGetBody(shape)) - 1;

    /// <summary>
    /// A query's filter: Box3D's default, accepting the shapes whose category, their body's motion, is among
    /// <paramref name="motions"/>.
    /// </summary>
    private static B3.QueryFilter FilterOf(Motions motions)
    {
        B3.QueryFilter filter = B3.DefaultQueryFilter();
        filter.MaskBits = (ulong)motions;
        return filter;
    }

    private static Motions MotionsOf(Motion motion)
    {
        return motion switch
        {
            Motion.Static => Motions.Static,
            Motion.Dynamic => Motions.Dynamic,
            Motion.Kinematic => Motions.Kinematic,
            _ => Motions.Static,
        };
    }

    private Handle<Model> ModelOf(Entity entity)
    {
        return _world.Has<Renderer>(entity) ? _world.Get<Renderer>(entity).Model : Handle<Model>.None;
    }

    /// <summary>
    /// The models the bodies among <paramref name="prefabs"/> are shaped like: each part with a <see cref="Physics"/>
    /// and a <see cref="Renderer"/> naming a model, and the parts under them. Any thread.
    /// </summary>
    private static HashSet<Handle<Model>> ModelsOf(IReadOnlyList<Prefab> prefabs)
    {
        HashSet<Handle<Model>> models = [];
        foreach (Prefab prefab in prefabs)
        {
            Add(prefab.Parts);
        }

        return models;

        void Add(IReadOnlyList<PrefabPart> parts)
        {
            foreach (PrefabPart part in parts)
            {
                Handle<Model> model = Handle<Model>.None;
                bool isBody = false;
                foreach (IComponent component in part.Components)
                {
                    isBody |= component is Physics;
                    model = component is Renderer renderer ? renderer.Model : model;
                }

                if (isBody && model != Handle<Model>.None)
                {
                    models.Add(model);
                }

                Add(part.Children);
            }
        }
    }

    /// <summary>
    /// Checks each domain whose models are being fitted: how many shapes are there tells how far it is, and once all
    /// are, whoever waits for it goes on and spawns it.
    /// </summary>
    private void CheckPreloads()
    {
        for (int i = _preloads.Count - 1; i >= 0; i--)
        {
            Preload preload = _preloads[i];
            int done = 0;
            foreach (Handle<Model> model in preload.Models)
            {
                if (_shapes.Get(model) is not null)
                {
                    done++;
                }
            }

            float share = preload.Models.Count == 0 ? 1 : (float)done / preload.Models.Count;
            if (share != preload.Reported)
            {
                preload.Reported = share;
                preload.Progress.Report(share);
            }

            if (done == preload.Models.Count)
            {
                _preloads.RemoveAt(i);
                preload.Done.TrySetResult();
            }
        }
    }

    private static B3.Vec3 ToVec(Vector3 vector) => new() { X = vector.X, Y = vector.Y, Z = vector.Z };

    private static Vector3 FromVec(B3.Vec3 vector) => new(vector.X, vector.Y, vector.Z);

    private static B3.Quat ToQuat(Quaternion rotation) => new() { V = new B3.Vec3 { X = rotation.X, Y = rotation.Y, Z = rotation.Z }, S = rotation.W };

    private static Quaternion FromQuat(B3.Quat rotation) => new(rotation.V.X, rotation.V.Y, rotation.V.Z, rotation.S);

    private static B3.Vec3 Scaled(B3.Vec3 vector, float scale) => new() { X = vector.X * scale, Y = vector.Y * scale, Z = vector.Z * scale };

    /// <summary>
    /// A failed Box3D assert is a bug, in the gem or in Box3D, that leaves the world in a state it cannot go on from:
    /// the process ends with a crash file, as the engine's own asserts do.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int OnAssert(byte* condition, byte* file, int line)
    {
        Debugging.Critical($"Box3D failed an assert, {CString.Read(condition)}, at {CString.Read(file)}:{line}.");
        return 1;
    }

    [UnmanagedCallersOnly]
    private static void OnLog(byte* message)
    {
        Debugging.Warn($"Box3D: {CString.Read(message)}");
    }

    /// <summary>
    /// Takes one shape an overlap found into the query's entities, and asks for more while there is room. A byte
    /// rather than a bool, which cannot cross into native code from a method called only from it; C's bool is a byte.
    /// </summary>
    [UnmanagedCallersOnly]
    private static byte OnOverlap(B3.ShapeId shape, void* context)
    {
        OverlapQuery* query = (OverlapQuery*)context;
        query->Found[query->Count++] = query->Bodies[SlotOf(shape)].Entity;
        return query->Count < query->Capacity ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// An entity's body, with its components as the body last saw them, which the next step's are compared with.
    /// </summary>
    private struct Body
    {
        public B3.BodyId Id;

        public Entity Entity;

        public Handle<Model> Model;

        public Transform Transform;

        public Physics Physics;

        public uint Seen;
    }

    /// <summary>
    /// A domain whose models are being fitted before it spawns: which, how far that is as last told, and the task
    /// whoever opens it awaits.
    /// </summary>
    private sealed class Preload(HashSet<Handle<Model>> models, IProgress<float> progress)
    {
        public HashSet<Handle<Model>> Models { get; } = models;

        public IProgress<float> Progress { get; } = progress;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public float Reported { get; set; } = -1;
    }

    /// <summary>
    /// An overlap in progress, handed to <see cref="OnOverlap"/> through Box3D: where the entities go, how many fit,
    /// how many are there, and the bodies to look them up in, pinned for the call.
    /// </summary>
    private struct OverlapQuery
    {
        public Body* Bodies;

        public Entity* Found;

        public int Capacity;

        public int Count;
    }
}
