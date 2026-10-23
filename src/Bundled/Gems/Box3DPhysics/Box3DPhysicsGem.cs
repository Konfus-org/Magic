using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Queries;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using static Magic.Box3DPhysics.B3;
using static Magic.Box3DPhysics.b3BodyType;

namespace Magic.Box3DPhysics;

/// <summary>
/// Moves entities as rigid bodies with Box3D. Every entity with a <see cref="Transform"/> and a <see cref="Physics"/>
/// gets a body shaped as the component says and sized to its model (<see cref="ModelShapes"/>); the body moves the
/// transform, and the components move the body: once per fixed step, after the scripts' own, each entity's components
/// are compared with the copy kept from the last step, so a transform a script moved moves the body there (a kinematic
/// body is driven to it, pushing what is in the way), a velocity it set is applied, and a change to anything that
/// shapes the body remakes it. Then the step is handed to a thread of the gem's own, which steps the world while the
/// frame goes on; what moved, and what the listening bodies touch, the main thread takes in at its next hook, each
/// body's position, rotation and velocities written back unless a script wrote them meanwhile, whose values then stand
/// and reach the body with the next step. A body whose entity or component is gone is destroyed at the next step, as is
/// one that has gone farther from every player camera than the settings' range, so a world full of bodies is simulated
/// only around the player; an entity past the range waits where it is, body or no body, until the player comes back.
/// The models a domain's bodies are shaped like are fitted before it spawns (<see cref="IPreloader"/>), so its bodies
/// are there from its first step; one that replaces the world has them made as it is readied, but takes no step until
/// the loading is over (<see cref="WorldLoading"/>), so nothing moves before the world is shown. A shape is kept while
/// a domain that took it in is open, or anything is shaped like it, and freed once a new world takes the old one's
/// place without it (<see cref="ForgetAsync"/>).
/// <para>
/// It answers <see cref="IPhysics"/> from the bodies as the last step left them, waiting for a step still running,
/// and after each step writes what every body that listens, one whose entity has a <see cref="Contacts"/>, is
/// touching, with the phase worked out against what the component held the step before.
/// </para>
/// <para>
/// The world is one thread's at a time: the physics thread's from a step's hand-over until it has stepped and read
/// what moved, the main thread's otherwise, which waits for the step first (<see cref="WaitForStep"/>). Several
/// fixed steps in one frame each wait for the one before, so the frame's last overlaps the rest of the frame.
/// </para>
/// <para>
/// Box3D works in metres, kilograms and seconds with +Y up, as the engine does.
/// </para>
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The shapes and the watch are the main thread's, the world's, and end in Stop there; "
        + "Dispose would run on the render thread. The step's event and semaphore hold no OS handle and outlive the "
        + "physics thread, which may still be waking as the run ends.")]
internal sealed class Box3DPhysicsGem : IGem, IPhysics, IPreloader
{
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

    private static readonly Meter _meter = new("Magic.Gems.Box3DPhysics");

    /// <summary>
    /// Buckets from a tenth of a millisecond to a second, as a step is measured against the fixed step.
    /// </summary>
    private static readonly InstrumentAdvice<double> _buckets = new()
    {
        HistogramBucketBoundaries
        = [0.0001, 0.00025, 0.0005, 0.001, 0.002, 0.004, 0.008, 1.0 / 60, 1.0 / 30, 0.05, 0.1, 0.25, 0.5, 1]
    };
    private static readonly Histogram<double> _stepDuration = _meter.CreateTimer(
        "magic.gems.box3d_physics.step.duration",
        "How long the physics thread took to step the world and read what moved.",
        _buckets);
    private static readonly Histogram<double> _stepWait = _meter.CreateTimer(
        "magic.gems.box3d_physics.step_wait.duration",
        "How long the main thread waited for a step still running, ahead of a fixed step, a query or the end.",
        _buckets);

    private readonly World _world;
    private readonly Threads _threads;
    private readonly Box3DPhysicsSettings _settings;
    private readonly ModelShapes _shapes;
    private readonly Subscription _watching;
    private readonly List<Body> _bodies = [];
    private readonly Dictionary<Entity, int> _slots = [];
    private readonly Stack<int> _free = [];
    private readonly List<Entity> _gone = [];
    private readonly List<Vector3> _cameras = [];

    /// <summary>
    /// The domains taken in, from when their models begin to be fitted until what was handed back is disposed: each
    /// checked every step until all are (<see cref="CheckPreloads"/>), then holding their shapes through a forget
    /// (<see cref="ForgetAsync"/>). Main thread.
    /// </summary>
    private readonly List<Preload> _preloads = [];

    /// <summary>
    /// The domains whose hold was disposed, on any thread, for the main thread to let go of as a step starts.
    /// </summary>
    private readonly Channel<Preload> _letGo = Channel.CreateUnbounded<Preload>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Set while no step is in flight: reset as one is handed over, set by the physics thread once it has stepped the
    /// world and read what moved.
    /// </summary>
    private readonly ManualResetEventSlim _stepped = new(initialState: true);

    /// <summary>
    /// Wakes the physics thread, for a step or to end.
    /// </summary>
    private readonly SemaphoreSlim _kick = new(0);

    /// <summary>
    /// The bodies whose contacts the step reads, by slot: those whose entity has a <see cref="Contacts"/>, listed by
    /// the main thread as it hands the step over.
    /// </summary>
    private readonly List<int> _listening = [];

    /// <summary>
    /// What the last step moved, read by the physics thread for the main thread to take in (<see cref="Apply"/>).
    /// </summary>
    private readonly List<Moved> _moves = [];

    /// <summary>
    /// What each listening body touches after the last step, by slot, read as the moves are.
    /// </summary>
    private readonly Dictionary<int, Contacts> _touched = [];
    private b3WorldId _box3d;

    /// <summary>
    /// Which step this is, stamped on each body seen in it: a body not stamped is of an entity that is gone, or out
    /// of range.
    /// </summary>
    private uint _step;

    /// <summary>
    /// The time and the substeps of the step handed over, for the physics thread.
    /// </summary>
    private float _stepSeconds;
    private int _substeps;

    /// <summary>
    /// The last step's results have been taken in: false from a hand-over until <see cref="Apply"/>. Main thread.
    /// </summary>
    private bool _isApplied = true;

    /// <summary>
    /// A domain replacing the world is being readied, and its bodies are made but no step is taken until the loading is
    /// told over (<see cref="ReadyAsync"/>). Main thread.
    /// </summary>
    private bool _isHeld;

    /// <summary>
    /// Ends the readying once a fixed step has made the domain's bodies; none when nothing is being readied. Main
    /// thread.
    /// </summary>
    private TaskCompletionSource? _readied;

    /// <summary>
    /// Ends the forgetting once a fixed step has freed the shapes the old world alone needed; none when no new world
    /// took the old one's place since. Main thread.
    /// </summary>
    private TaskCompletionSource? _forgetting;

    /// <summary>
    /// The run is ending: the physics thread returns when next woken.
    /// </summary>
    private volatile bool _isEnding;

    public unsafe Box3DPhysicsGem(
        World world,
        Assets assets,
        Threads threads,
        Events events,
        Box3DPhysicsSettings settings)
    {
        _world = world;
        _threads = threads;
        _settings = settings;
        _shapes = new ModelShapes(assets);
        _watching = events.Watch<WorldLoading>(loading => _isHeld &= loading.IsLoading);

        b3SetAssertFcn(&OnAssert);
        b3SetLogFcn(&OnLog);
        b3WorldDef def = b3DefaultWorldDef();
        def.gravity = new b3Vec3 { y = -settings.Gravity };
        def.workerCount = (uint)Math.Clamp(settings.Workers, 1, B3_MAX_WORKERS);
        _box3d = b3CreateWorld(&def);

        b3Version version = b3GetVersion();
        Debugging.Info($"Box3D {version.major}.{version.minor}.{version.revision} is simulating, "
            + $"on a thread of its own, with {def.workerCount} solver thread(s).");
        threads.RunAsync("Physics", RunSteps).FireAndForget();
    }

    /// <summary>
    /// Takes in the last step's results once the step has ended, if the fixed step has not yet, so a script's Update
    /// sees the bodies as the step left them.
    /// </summary>
    public void Update(in Frame frame)
    {
        if (!_isApplied && _stepped.IsSet) Apply();
    }

    public void FixedUpdate(in Frame frame)
    {
        // The step before is waited for and taken in, so the world is the main thread's again.
        WaitForStep();
        if (!_isApplied) Apply();

        // A new step: the shapes fitted since the last are taken in, the domains let go of since no longer hold theirs,
        // those waiting on theirs are told how far they are, and gravity follows its setting.
        _step++;
        _shapes.TakeArrived();
        while (_letGo.Reader.TryRead(out Preload? letGo))
        {
            _preloads.Remove(letGo);
        }

        CheckPreloads();
        if (_settings.Gravity != -b3World_GetGravity(_box3d).y)
            b3World_SetGravity(_box3d, new b3Vec3 { y = -_settings.Gravity });

        // Where the player's cameras are, which the range is measured from.
        _cameras.Clear();
        foreach ((_, _, Span<Transform> transforms, Span<Tags> tags) in _world.Query<Camera, Transform, Tags>())
        {
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i].Has(Tag.Player)) _cameras.Add(transforms[i].Position);
            }
        }

        // Each entity that could be in range makes its body, or carries its changes over to it, and stamps it seen.
        float farthest = _settings.Range * Leave;
        foreach (EcsChunk<Transform, Physics> chunk in _world.Query<Transform, Physics>())
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Physics> physics) = chunk;
            for (int i = 0; i < entities.Length; i++)
            {
                if (CanBeNear(transforms[i], farthest))
                    Follow(entities[i], ref transforms[i], ref physics[i], frame.DeltaSeconds);
            }
        }

        // A body not stamped this step is of an entity gone, or out of range, and is dropped.
        foreach ((Entity entity, int slot) in _slots)
        {
            if (_bodies[slot].Seen != _step) _gone.Add(entity);
        }

        foreach (Entity entity in _gone)
        {
            Destroy(entity);
        }

        if (_gone.Count > 0)
            Debugging.Verbose($"{_gone.Count} bodies dropped: their entities gone, or out of range of the player.");

        _gone.Clear();

        // A new world took the old one's place: with the bodies of what went with it dropped, the shapes nothing left
        // needs are freed before the step can read any of them.
        if (_forgetting is { } forgetting)
        {
            Forget();
            forgetting.TrySetResult();
            _forgetting = null;
        }

        if (!_isHeld)
        {
            Kick(frame.DeltaSeconds);

            return;
        }

        // The domain's bodies are made, so its first step, once it is shown, has only to step them.
        _readied?.TrySetResult();
        _readied = null;
    }

    /// <summary>
    /// Waits for a step still running, ends the physics thread, then destroys the world, and every body with it, and
    /// the shapes the bodies shared: on the main thread, the world's, as the run ends.
    /// </summary>
    public void Stop()
    {
        _watching.Dispose();
        WaitForStep();
        _isEnding = true;
        _kick.Release();
        b3DestroyWorld(_box3d);
        _shapes.Dispose();
    }

    public RayHit? RayCast(Vector3 from, Vector3 to, Motions motions = Motions.All)
    {
        WaitForStep();
        b3RayResult result = b3World_CastRayClosest(_box3d, ToVec(from), ToVec(to - from), FilterOf(motions));

        if (!result.hit) return null;

        Entity entity = _bodies[GetSlot(result.shapeId)].Entity;

        return new RayHit(
            entity,
            FromVec(result.point),
            FromVec(result.normal),
            result.fraction * Vector3.Distance(from, to));
    }

    public unsafe int SphereCast(Vector3 center, float radius, Span<Entity> found, Motions motions = Motions.All)
    {
        if (found.IsEmpty) return 0;

        // The sphere is a point at the origin with the radius, which the overlap places at the centre. Each shape it
        // overlaps has its entity written straight into found, from the bodies pinned for the call; the callback is
        // C's byte one, cast to the bool the binding takes.
        WaitForStep();
        b3Vec3 point = default;
        b3ShapeProxy proxy = new() { points = &point, count = 1, radius = radius };
        OverlapQuery query = new() { Capacity = found.Length };
        fixed (Entity* into = found)
        fixed (Body* bodies = CollectionsMarshal.AsSpan(_bodies))
        {
            query.Found = into;
            query.Bodies = bodies;
            b3World_OverlapShape(
                _box3d,
                ToVec(center),
                &proxy,
                FilterOf(motions),
                (delegate* unmanaged[Cdecl]<b3ShapeId, void*, bool>)
                    (delegate* unmanaged[Cdecl]<b3ShapeId, void*, byte>)&OnOverlap,
                &query);
        }

        return query.Count;
    }

    /// <summary>
    /// Frees, at the next fixed step, the shapes the old world alone needed: once that step has dropped the bodies of
    /// the entities destroyed with it, every shape that no entity with a <see cref="Physics"/> left and no domain still
    /// held is shaped like (<see cref="Forget"/>). Ends once they are freed.
    /// </summary>
    public Task ForgetAsync(CancellationToken cancel)
    {
        _forgetting ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        return _forgetting.Task.WaitAsync(cancel);
    }

    /// <summary>
    /// Fits the shape of every model a body among <paramref name="prefabs"/> is shaped like, before they spawn, so
    /// each gets its body in its first step rather than waiting for its model. Done once every shape is there; what it
    /// hands back holds them, so no forget frees them, until it is disposed.
    /// </summary>
    public async Task<Subscription> PreloadAsync(
        IReadOnlyList<Prefab> prefabs,
        IProgress<float> progress,
        CancellationToken cancel)
    {
        HashSet<Handle<Model>> models = GetModels(prefabs);
        Preload preload = new(models, progress, cancel);
        await _threads.PostAsync(
            EngineThread.Main,
            () =>
            {
                foreach (Handle<Model> model in models)
                {
                    _shapes.Get(model);
                }

                _preloads.Add(preload);
            },
            cancel).ConfigureAwait(false);
        await preload.Done.Task.ConfigureAwait(false);

        return new Subscription(() => _letGo.Writer.TryWrite(preload));
    }

    /// <summary>
    /// Makes the domain's bodies at the next fixed step, their shapes fitted before it spawned, and takes no step from
    /// now until the loading is told over (<see cref="WorldLoading"/>), the frame after the world is first shown, so
    /// nothing has fallen or moved by then, and the first step has only to step them. Ready once they are made.
    /// </summary>
    public Task ReadyAsync(CancellationToken cancel)
    {
        _isHeld = true;
        TaskCompletionSource readied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _readied?.TrySetCanceled(CancellationToken.None);
        _readied = readied;

        return readied.Task.WaitAsync(cancel);
    }

    internal static b3Vec3 ToVec(Vector3 vector) => new() { x = vector.X, y = vector.Y, z = vector.Z };

    private static Vector3 FromVec(b3Vec3 vector) => new(vector.x, vector.y, vector.z);

    private static b3Quat ToQuat(Quaternion rotation)
        => new() { v = new b3Vec3 { x = rotation.X, y = rotation.Y, z = rotation.Z }, s = rotation.W };

    private static Quaternion FromQuat(b3Quat rotation) => new(rotation.v.x, rotation.v.y, rotation.v.z, rotation.s);

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
    /// Adds the shape to the body: the model's bounds at the entity's scale as a box, a sphere or a capsule, or its
    /// triangles at that scale. A bound with no size, a plane's thickness, is given a little, as a shape needs some.
    /// </summary>
    private static unsafe void AddShape(b3BodyId body, b3ShapeDef* def, ModelShape shape, Shape kind, Vector3 scale)
    {
        const float Least = 0.01f;
        scale = Vector3.Abs(scale);
        Vector3 half = Vector3.Max((shape.Max - shape.Min) * scale * 0.5f, new Vector3(Least));
        Vector3 centre = (shape.Max + shape.Min) * scale * 0.5f;
        switch (kind)
        {
            case Shape.Sphere:
                b3Sphere sphere = new()
                {
                    center = ToVec(centre),
                    radius = Math.Max(half.X, Math.Max(half.Y, half.Z))
                };
                b3CreateSphereShape(body, def, &sphere);
                break;
            case Shape.Capsule:
                // Upright, as wide as the wider of the bounds' X and Z, its two centres as far apart as the height
                // leaves once the round ends are taken off.
                float radius = Math.Max(half.X, half.Z);
                float reach = Math.Max(half.Y - radius, 0);
                b3Capsule capsule = new()
                {
                    center1 = ToVec(centre - new Vector3(0, reach, 0)),
                    center2 = ToVec(centre + new Vector3(0, reach, 0)),
                    radius = radius,
                };
                b3CreateCapsuleShape(body, def, &capsule);
                break;
            case Shape.Mesh:
                b3CreateMeshShape(body, def, shape.Mesh, ToVec(scale));
                break;
            case Shape.Box:
            default:
                b3BoxHull box = b3MakeOffsetBoxHull(half.X, half.Y, half.Z, ToVec(centre));
                b3CreateHullShape(body, def, &box.@base);
                break;
        }
    }

    /// <summary>
    /// Writes what <paramref name="entity"/>'s body touches after the last step, <paramref name="read"/>, over its
    /// contacts: each Stay when the component already held it, else Enter; then each the component held and the body
    /// no longer touches, as Exit for this step. An entity with no body, out of range, parts from them all.
    /// </summary>
    private static void Touch(Entity entity, ref Contacts contacts, in Contacts read)
    {
        Contacts now = default;
        int written = 0;
        foreach (Contact touching in read)
        {
            if (touching.Phase == ContactPhase.None) continue;

            Contact contact = touching;
            contact.Phase = GetPhase(contacts, contact.Other) is ContactPhase.Enter or ContactPhase.Stay
                ? ContactPhase.Stay
                : ContactPhase.Enter;
            if (contact.Phase == ContactPhase.Enter)
            {
                Debugging.Verbose($"Entity {entity.Id} touched entity {contact.Other.Id} at ({contact.Point.X:0.###}, "
                    + $"{contact.Point.Y:0.###}, {contact.Point.Z:0.###}), {contact.Impulse:0.###} Ns.");
            }

            now[written++] = contact;
        }

        foreach (Contact held in contacts)
        {
            if (held.Phase is ContactPhase.None or ContactPhase.Exit || GetPhase(now, held.Other) != ContactPhase.None)
                continue;

            if (written == Contacts.Capacity) break;

            now[written++] = held with { Phase = ContactPhase.Exit, Impulse = 0 };
            Debugging.Verbose($"Entity {entity.Id} parted from entity {held.Other.Id}.");
        }

        contacts = now;
    }

    private static ContactPhase GetPhase(in Contacts contacts, Entity other)
    {
        foreach (Contact held in contacts)
        {
            if (held.Phase != ContactPhase.None && held.Other == other) return held.Phase;
        }

        return ContactPhase.None;
    }

    /// <summary>
    /// The body slot a shape belongs to, kept one up in its body's user data so that 0 is no body.
    /// </summary>
    private static unsafe int GetSlot(b3ShapeId shape) => (int)(nuint)b3Body_GetUserData(b3Shape_GetBody(shape)) - 1;

    /// <summary>
    /// A query's filter: Box3D's default, accepting the shapes whose category, their body's motion, is among
    /// <paramref name="motions"/>.
    /// </summary>
    private static b3QueryFilter FilterOf(Motions motions)
    {
        b3QueryFilter filter = b3DefaultQueryFilter();
        filter.maskBits = (ulong)motions;

        return filter;
    }

    private static Motions GetMotions(Motion motion)
    {
        return motion switch
        {
            Motion.Static => Motions.Static,
            Motion.Dynamic => Motions.Dynamic,
            Motion.Kinematic => Motions.Kinematic,
            _ => Motions.Static,
        };
    }

    /// <summary>
    /// What a <see cref="Surface"/> is to Box3D: a friction coefficient, from ice's to glue's, and for the stickiest a
    /// little rolling resistance, so a sticky ball stops rolling. Two surfaces that touch take the geometric mean of
    /// their frictions and the larger of their bounces, so a sticky box on ice still slides, a little.
    /// </summary>
    private static b3SurfaceMaterial GetMaterial(Surface surface, float bounce)
    {
        b3SurfaceMaterial material = b3DefaultSurfaceMaterial();
        (material.friction, material.rollingResistance) = surface switch
        {
            Surface.Slippery => (0.05f, 0f),
            Surface.Slick => (0.2f, 0f),
            Surface.Grippy => (0.9f, 0.01f),
            Surface.Sticky => (1.5f, 0.05f),
            Surface.Normal => (0.6f, 0f),
            _ => (0.6f, 0f),
        };
        material.restitution = Math.Clamp(bounce, 0, 1);

        return material;
    }

    /// <summary>
    /// The models the bodies among <paramref name="prefabs"/> are shaped like: each part with a <see cref="Physics"/>
    /// and a <see cref="Renderer"/> naming a model, and the parts under them. Any thread.
    /// </summary>
    private static HashSet<Handle<Model>> GetModels(IReadOnlyList<Prefab> prefabs)
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

                if (isBody && model != Handle<Model>.None) models.Add(model);

                Add(part.Children);
            }
        }
    }

    private static b3Vec3 Scale(b3Vec3 vector, float scale)
        => new() { x = vector.x * scale, y = vector.y * scale, z = vector.z * scale };

    /// <summary>
    /// A failed Box3D assert is a bug, in the gem or in Box3D, that leaves the world in a state it cannot go on from:
    /// the process ends with a crash file, as the engine's own asserts do.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnAssert(byte* condition, byte* file, int line)
    {
        Debugging.Critical($"Box3D failed an assert, {CString.Read(condition)}, at {CString.Read(file)}:{line}.");

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnLog(byte* message) => Debugging.Warn($"Box3D: {CString.Read(message)}");

    /// <summary>
    /// Takes one shape an overlap found into the query's entities, and asks for more while there is room. A byte
    /// rather than a bool, which cannot cross into native code from a method called only from it; C's bool is a byte.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte OnOverlap(b3ShapeId shape, void* context)
    {
        OverlapQuery* query = (OverlapQuery*)context;
        query->Found[query->Count++] = query->Bodies[GetSlot(shape)].Entity;

        return query->Count < query->Capacity ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Makes the body for a new entity in range of a player camera, or carries what its components changed since the
    /// last step over to its body; one out of range is left unstamped, for the sweep to drop.
    /// </summary>
    private void Follow(Entity entity, ref Transform transform, ref Physics physics, float step)
    {
        Handle<Model> model = GetModel(entity);
        ModelShape? shape = _shapes.Get(model);
        bool hasBody = _slots.TryGetValue(entity, out int slot);

        if (!IsNear(transform, shape, hasBody ? _settings.Range * Leave : _settings.Range)) return;

        if (!hasBody)
        {
            Create(entity, model, shape, transform, physics);

            return;
        }

        ref Body body = ref CollectionsMarshal.AsSpan(_bodies)[slot];
        body.Seen = _step;
        if (IsRemade(body, model, transform, physics))
        {
            Destroy(entity);
            Create(entity, model, shape, transform, physics);

            return;
        }

        // A kinematic body is driven to where its transform is, pushing what is in the way; any other body whose
        // transform moved is put there and woken.
        bool isMoved = transform.Position != body.Transform.Position || transform.Rotation != body.Transform.Rotation;
        if (physics.Motion == Motion.Kinematic)
        {
            b3Transform target = new() { p = ToVec(transform.Position), q = ToQuat(transform.Rotation) };
            b3Body_SetTargetTransform(body.Id, target, step, isMoved);
        }
        else if (isMoved)
        {
            b3Body_SetTransform(body.Id, ToVec(transform.Position), ToQuat(transform.Rotation));
            b3Body_SetAwake(body.Id, true);
        }

        if (physics.Velocity != body.Physics.Velocity)
        {
            b3Body_SetLinearVelocity(body.Id, ToVec(physics.Velocity));
            b3Body_SetAwake(body.Id, true);
        }

        if (physics.AngularVelocity != body.Physics.AngularVelocity)
        {
            b3Body_SetAngularVelocity(body.Id, ToVec(physics.AngularVelocity));
            b3Body_SetAwake(body.Id, true);
        }

        body.Transform = transform;
        body.Physics = physics;
    }

    /// <summary>
    /// Whether the entity could be within <paramref name="range"/> of a player camera, from its transform alone: its
    /// centre's distance less the farthest any shape reaches at its scale (<see cref="ModelShapes.Longest"/>), so
    /// whatever <see cref="IsNear"/> would call near passes, and the step never looks up the model, the shape or the
    /// body of the many entities far from every player, which is most of a world. With no player camera, everything
    /// could be.
    /// </summary>
    private bool CanBeNear(in Transform transform, float range)
    {
        if (_cameras.Count == 0) return true;

        float limit = range + (transform.Scale.Length() * _shapes.Longest);
        float limitSquared = limit * limit;
        foreach (Vector3 camera in _cameras)
        {
            if (Vector3.DistanceSquared(transform.Position, camera) < limitSquared) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the entity's shape reaches within <paramref name="range"/> of a player camera: its centre's distance
    /// less how far the shape reaches from it, so the ground is near from anywhere on it. With no player camera,
    /// everything is near.
    /// </summary>
    private bool IsNear(in Transform transform, ModelShape? shape, float range)
    {
        if (_cameras.Count == 0) return true;

        // How far the shape reaches from the entity's position: half its bounds' diagonal, plus how far their centre is
        // from it; a shape still loading is taken to reach the scale's length.
        Vector3 scale = Vector3.Abs(transform.Scale);
        float reach = shape is null
            ? scale.Length()
            : (((shape.Max - shape.Min) * scale).Length() + ((shape.Max + shape.Min) * scale).Length()) * 0.5f;
        foreach (Vector3 camera in _cameras)
        {
            if (Vector3.Distance(transform.Position, camera) - reach < range) return true;
        }

        return false;
    }

    /// <summary>
    /// Makes the entity's body and shape, once its model's shape is there; until then the entity waits, unmoved.
    /// </summary>
    private unsafe void Create(
        Entity entity,
        Handle<Model> model,
        ModelShape? shape,
        in Transform transform,
        in Physics physics)
    {
        if (shape is null) return;

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

        b3BodyDef def = b3DefaultBodyDef();
        def.type = physics.Motion switch
        {
            Motion.Dynamic => b3_dynamicBody,
            Motion.Kinematic => b3_kinematicBody,
            Motion.Static => b3_staticBody,
            _ => b3_staticBody,
        };
        def.position = ToVec(transform.Position);
        def.rotation = ToQuat(transform.Rotation);
        def.linearVelocity = ToVec(physics.Velocity);
        def.angularVelocity = ToVec(physics.AngularVelocity);
        def.userData = (void*)(nuint)(slot + 1);
        b3BodyId id = b3CreateBody(_box3d, &def);
        b3ShapeDef shapeDef = b3DefaultShapeDef();
        shapeDef.baseMaterial = GetMaterial(physics.Surface, physics.Bounce);
        shapeDef.filter.categoryBits = (ulong)GetMotions(physics.Motion);
        Shape kind = physics.Shape;
        if (kind == Shape.Mesh && (physics.Motion != Motion.Static || shape.Mesh is null))
        {
            Debugging.Error(physics.Motion != Motion.Static
                ? $"Entity {entity.Id}'s Physics asks for a mesh shape on a body that moves, "
                    + "which only a static body can have, so it is a box."
                : $"Entity {entity.Id}'s Physics asks for a mesh shape, "
                    + "but its model has no triangles, so it is a box.");
            kind = Shape.Box;
        }

        AddShape(id, &shapeDef, shape, kind, transform.Scale);

        // A mass asked for scales the one Box3D worked out from the shape, and the inertia with it, so the body still
        // turns as its shape says.
        if (physics.Mass > 0 && physics.Motion == Motion.Dynamic)
        {
            b3MassData mass = b3Body_GetMassData(id);
            if (mass.mass > 0)
            {
                float scale = physics.Mass / mass.mass;
                mass.mass = physics.Mass;
                mass.inertia.cx = Scale(mass.inertia.cx, scale);
                mass.inertia.cy = Scale(mass.inertia.cy, scale);
                mass.inertia.cz = Scale(mass.inertia.cz, scale);
                b3Body_SetMassData(id, mass);
            }
        }

        _bodies[slot] = new Body
        {
            Id = id,
            Entity = entity,
            Model = model,
            Transform = transform,
            Physics = physics,
            Seen = _step
        };
        _slots.Add(entity, slot);
        Debugging.Verbose($"Entity {entity.Id} has a {physics.Motion} {kind} body, {b3Body_GetMass(id):0.###} kg, "
            + $"at ({transform.Position.X:0.###}, {transform.Position.Y:0.###}, {transform.Position.Z:0.###}).");
    }

    private void Destroy(Entity entity)
    {
        if (!_slots.Remove(entity, out int slot)) return;

        b3DestroyBody(_bodies[slot].Id);
        _bodies[slot] = default;
        _free.Push(slot);
    }

    /// <summary>
    /// Hands the physics thread the step: which bodies' contacts to read after it, the time and the substeps, then
    /// wakes it. The world is that thread's until the step has ended.
    /// </summary>
    private void Kick(float seconds)
    {
        _listening.Clear();
        foreach ((ReadOnlySpan<Entity> entities, _) in _world.Query<Contacts>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (_slots.TryGetValue(entities[i], out int slot)) _listening.Add(slot);
            }
        }

        _stepSeconds = seconds;
        _substeps = _settings.Substeps;
        _isApplied = false;
        _stepped.Reset();
        _kick.Release();
    }

    /// <summary>
    /// Waits for a step still running, so the world is the main thread's; timed while something listens.
    /// </summary>
    private void WaitForStep()
    {
        if (_stepped.IsSet) return;

        long start = _stepWait.Start();
        _stepped.Wait();
        _stepWait.RecordSince(start);
    }

    /// <summary>
    /// The physics thread: each step it is woken for, it steps the world and reads what moved and what the listening
    /// bodies touch, for the main thread; until the gem ends it.
    /// </summary>
    private void RunSteps(CancellationToken _)
    {
        while (true)
        {
            // Not the run's stopping token: the thread ends through Stop() alone, after the last step is waited for
            // and before the world is destroyed. Were it to leave on the token, a kick between the run starting to end
            // and Stop() would reset the step event with no thread left to set it, and WaitForStep would wait forever.
            _kick.Wait(CancellationToken.None);

            if (_isEnding) return;

            long start = _stepDuration.Start();
            Step();
            _stepDuration.RecordSince(start);

            _stepped.Set();
        }
    }

    /// <summary>
    /// Steps the world, then reads every body that moved and what each listening body touches into the results the
    /// main thread takes in. Physics thread.
    /// </summary>
    private unsafe void Step()
    {
        b3World_Step(_box3d, _stepSeconds, _substeps);
        b3BodyEvents events = b3World_GetBodyEvents(_box3d);
        _moves.Clear();
        for (int i = 0; i < events.moveCount; i++)
        {
            ref b3BodyMoveEvent moved = ref events.moveEvents[i];
            int slot = (int)(nuint)moved.userData - 1;
            b3BodyId id = _bodies[slot].Id;
            _moves.Add(new Moved(
                slot,
                FromVec(moved.transform.p),
                FromQuat(moved.transform.q),
                FromVec(b3Body_GetLinearVelocity(id)),
                FromVec(b3Body_GetAngularVelocity(id)),
                moved.fellAsleep));
        }

        _touched.Clear();
        foreach (int slot in _listening)
        {
            _touched[slot] = GetTouching(slot);
        }
    }

    /// <summary>
    /// What the body in <paramref name="slot"/> touches after the step, each as <see cref="ContactPhase.Stay"/>: the
    /// main thread works the phase out against what the component held. Physics thread.
    /// </summary>
    private unsafe Contacts GetTouching(int slot)
    {
        Contacts now = default;
        int written = 0;
        b3BodyId id = _bodies[slot].Id;
        int capacity = Math.Min(b3Body_GetContactCapacity(id), MostTouching);
        b3ContactData* data = stackalloc b3ContactData[capacity];
        int count = b3Body_GetContactData(id, data, capacity);
        for (int i = 0; i < count && written < Contacts.Capacity; i++)
        {
            Contact contact = GetContact(slot, data[i]);
            if (contact.Phase != ContactPhase.None) now[written++] = contact;
        }

        return now;
    }

    /// <summary>
    /// Takes in what the last step moved and what the listening bodies touch. Each body that moved writes its position,
    /// rotation and velocities to its components, unless a script wrote one since the step was handed over: the
    /// script's value stands, and the next step carries it to the body. Then each entity's contacts are phased against
    /// what it held (<see cref="Touch"/>). Main thread.
    /// </summary>
    private void Apply()
    {
        _isApplied = true;
        Span<Body> bodies = CollectionsMarshal.AsSpan(_bodies);
        foreach (ref readonly Moved moved in CollectionsMarshal.AsSpan(_moves))
        {
            ref Body body = ref bodies[moved.Slot];

            if (!_world.IsAlive(body.Entity)) continue;

            ref Transform transform = ref _world.Get<Transform>(body.Entity);
            if (transform.Position == body.Transform.Position && transform.Rotation == body.Transform.Rotation)
            {
                transform.Position = moved.Position;
                transform.Rotation = moved.Rotation;
                body.Transform = transform;
            }

            ref Physics physics = ref _world.Get<Physics>(body.Entity);
            if (physics.Velocity == body.Physics.Velocity)
            {
                physics.Velocity = moved.Velocity;
                body.Physics.Velocity = moved.Velocity;
            }

            if (physics.AngularVelocity == body.Physics.AngularVelocity)
            {
                physics.AngularVelocity = moved.AngularVelocity;
                body.Physics.AngularVelocity = moved.AngularVelocity;
            }

            if (moved.FellAsleep)
            {
                Debugging.Verbose($"Entity {body.Entity.Id}'s body came to rest at ({moved.Position.X:0.###}, "
                    + $"{moved.Position.Y:0.###}, {moved.Position.Z:0.###}).");
            }
        }

        foreach ((ReadOnlySpan<Entity> entities, Span<Contacts> contacts) in _world.Query<Contacts>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                Contacts read = default;
                if (_slots.TryGetValue(entities[i], out int slot)) _touched.TryGetValue(slot, out read);

                Touch(entities[i], ref contacts[i], read);
            }
        }
    }

    /// <summary>
    /// One of Box3D's touching contacts as the body in <paramref name="slot"/> sees it: the other body's entity, the
    /// mean of the contact points, the normal turned to point away from the other body, and the impulse along it;
    /// a contact with no points, the two only near, is <see cref="ContactPhase.None"/>.
    /// </summary>
    private unsafe Contact GetContact(int slot, in b3ContactData data)
    {
        bool isA = GetSlot(data.shapeIdA) == slot;
        int other = isA ? GetSlot(data.shapeIdB) : GetSlot(data.shapeIdA);
        Vector3 centre = FromVec(b3Body_GetWorldCenterOfMass(b3Shape_GetBody(data.shapeIdA)));
        Vector3 point = Vector3.Zero;
        Vector3 normal = Vector3.Zero;
        float impulse = 0;
        int points = 0;

        // Every point of every manifold summed, for the mean point and normal and the whole impulse; a point's anchor
        // is measured from the first body's centre of mass.
        for (int i = 0; i < data.manifoldCount; i++)
        {
            ref b3Manifold manifold = ref data.manifolds[i];
            for (int j = 0; j < manifold.pointCount; j++)
            {
                point += centre + FromVec(manifold.points[j].anchorA);
                impulse += manifold.points[j].totalNormalImpulse;
                normal += FromVec(manifold.normal);
                points++;
            }
        }

        if (points == 0) return default;

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

    private Handle<Model> GetModel(Entity entity)
        => _world.Has<Renderer>(entity) ? _world.Get<Renderer>(entity).Model : Handle<Model>.None;

    /// <summary>
    /// Checks each domain whose models are being fitted: one whose open was cancelled is let go, and whoever waited is
    /// told; else how many shapes are there tells how far it is, and once all are, whoever waits for it goes on and
    /// spawns it, while the domain holds them.
    /// </summary>
    private void CheckPreloads()
    {
        for (int i = _preloads.Count - 1; i >= 0; i--)
        {
            Preload preload = _preloads[i];

            if (preload.Done.Task.IsCompleted) continue;

            if (preload.Cancel.IsCancellationRequested)
            {
                _preloads.RemoveAt(i);
                preload.Done.TrySetCanceled(preload.Cancel);

                continue;
            }

            int done = 0;
            foreach (Handle<Model> model in preload.Models)
            {
                if (_shapes.Get(model) is not null) done++;
            }

            float share = preload.Models.Count == 0 ? 1 : (float)done / preload.Models.Count;
            if (share != preload.Reported)
            {
                preload.Reported = share;
                preload.Progress.Report(share);
            }

            if (done == preload.Models.Count) preload.Done.TrySetResult();
        }
    }

    /// <summary>
    /// Frees every shape that no entity with a <see cref="Physics"/> and no domain still held is shaped like
    /// (<see cref="ModelShapes.Forget"/>). Every body left is shaped like one of those entities' models: the step has
    /// just dropped each body it did not follow, and remade or dropped any whose model changed. Main thread, between
    /// steps.
    /// </summary>
    private void Forget()
    {
        HashSet<Handle<Model>> kept = [];
        foreach ((ReadOnlySpan<Entity> entities, _) in _world.Query<Physics>())
        {
            foreach (Entity entity in entities)
            {
                kept.Add(GetModel(entity));
            }
        }

        foreach (Preload preload in _preloads)
        {
            kept.UnionWith(preload.Models);
        }

        int freed = _shapes.Forget(kept);
        if (freed > 0) Debugging.Verbose($"{freed} shapes freed, which only the world before needed.");
    }

    /// <summary>
    /// Where a body is and how it moves after a step, read by the physics thread for the main thread to write to the
    /// body's components.
    /// </summary>
    private readonly record struct Moved(
        int Slot,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Velocity,
        Vector3 AngularVelocity,
        bool FellAsleep);

    /// <summary>
    /// An entity's body, with its components as the body last saw them, which the next step's are compared with.
    /// </summary>
    private struct Body
    {
        internal b3BodyId Id;

        internal Entity Entity;

        internal Handle<Model> Model;

        internal Transform Transform;

        internal Physics Physics;

        internal uint Seen;
    }

    /// <summary>
    /// An overlap in progress, handed to <see cref="OnOverlap"/> through Box3D: where the entities go, how many fit,
    /// how many are there, and the bodies to look them up in, pinned for the call.
    /// </summary>
    private unsafe struct OverlapQuery
    {
        internal Body* Bodies;

        internal Entity* Found;

        internal int Capacity;

        internal int Count;
    }

    /// <summary>
    /// A domain taken in: the models its bodies are shaped like, fitted before it spawns and held until it lets them
    /// go, how far the fitting is as last told, the task whoever opens it awaits, and the token that cancels the open,
    /// checked each step until it is done.
    /// </summary>
    private sealed class Preload(HashSet<Handle<Model>> models, IProgress<float> progress, CancellationToken cancel)
    {
        internal HashSet<Handle<Model>> Models { get; } = models;

        internal IProgress<float> Progress { get; } = progress;

        internal CancellationToken Cancel { get; } = cancel;

        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal float Reported { get; set; } = -1;
    }
}
