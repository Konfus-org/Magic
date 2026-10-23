using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// An instance: where it is and what it draws.
/// </summary>
internal readonly record struct InstanceCopy(Transform Transform, Renderer Renderer);

/// <summary>
/// A camera, with the entity it is on, which names its view from one frame to the next.
/// </summary>
internal readonly record struct CameraCopy(Entity Entity, Transform Transform, Camera Camera);

/// <summary>
/// A point or spot light: where it is, its intensity in candela times its colour, and for a spot its axis and the scale
/// and offset of its cone's falloff; whether it casts shadows.
/// </summary>
internal readonly record struct LightCopy(
    Vector3 Position,
    Vector3 Forward,
    Vector3 Intensity,
    float Range,
    float Radius,
    float SpotScale,
    float SpotOffset,
    bool IsSpot,
    bool Shadowless);

/// <summary>
/// The sky's sun or moon: the way its light travels, its illuminance in lux times its colour, and its disc's angular
/// radius in radians.
/// </summary>
internal readonly record struct SkyLightCopy(Vector3 Direction, Vector3 Illuminance, float AngularRadius);

/// <summary>
/// A directional light that lights the world alone: the way its light travels, and its illuminance in lux times its
/// colour.
/// </summary>
internal readonly record struct DirectionalCopy(Vector3 Direction, Vector3 Illuminance);

/// <summary>
/// The directional light that casts the shadows: the way its light travels, its illuminance in lux times its colour,
/// its disc's angular radius in radians, which softens its shadows, and whether it is the sky's sun or moon, whose
/// light reaches the world through the sky's air and clouds.
/// </summary>
internal readonly record struct KeyLightCopy(
    Vector3 Direction,
    Vector3 Illuminance,
    float AngularRadius,
    bool IsSkyLight);

/// <summary>
/// The world as the GPU draws it, in two halves: the copy of it the render thread reads, and the rows the GPU reads
/// made from that copy (<see cref="UpdateRows"/>), the instances' (<see cref="Instances"/>) and the lights'
/// (<see cref="Lights"/>).
/// <para>
/// The world is copied twice over: on the main thread in LateUpdate, into the copy Render is not reading
/// (<see cref="Renderables"/>), while the render thread may still be drawing the frame before; the two are swapped in
/// Sync, when both threads hold still, and Render reads <see cref="Front"/> while the main thread is busy with the next
/// frame, which writes only the other. One system reads what the world has one of, or a few, and starts the copy
/// (<see cref="ReadWorld"/>); the instances and lights, of which there are many, are copied by systems on the ECS's
/// threads after it, kept at their entity's slot, and only what changed is passed on (<see cref="Delta{T}"/>), so a
/// frame costs the render thread what moved, not what there is: the copy itself is a comparison per entity, on every
/// thread, counted once per chunk.
/// </para>
/// </summary>
internal sealed class WorldProcessor : IDisposable
{
    private readonly List<Subscription> _systems = [];
    private readonly Slots<InstanceCopy> _instances;
    private readonly Slots<LightCopy> _lights;

    /// <summary>
    /// The copy LateUpdate writes. Main thread, and the ECS's threads under it.
    /// </summary>
    private Renderables _back = new();

    internal WorldProcessor(World world, GpuResources resources, FailureHandler failures, Assets assets)
    {
        Instances = new Instances(resources, failures, assets);
        Lights = new WorldLights(resources);
        _instances = new Slots<InstanceCopy>(_back.Instances);
        _lights = new Slots<LightCopy>(_back.Lights);

        // In this order: the ECS runs a phase's systems in the order they were added, and the first starts the copy.
        _systems.Add(world.Register(new ReadWorld(this, world)));
        _systems.Add(world.Register(new InstanceCopier(_instances)));
        _systems.Add(world.Register(new PointLightCopier(_lights)));
        _systems.Add(world.Register(new SpotLightCopier(_lights)));
    }

    /// <summary>
    /// The copy Render reads: the frame LateUpdate last finished, swapped in by <see cref="Swap"/>. Render thread.
    /// </summary>
    internal Renderables Front { get; private set; } = new();

    /// <summary>
    /// The instances' rows. Render thread.
    /// </summary>
    internal Instances Instances { get; }

    /// <summary>
    /// The point and spot lights' rows. Render thread.
    /// </summary>
    internal WorldLights Lights { get; }

    public void Dispose()
    {
        foreach (Subscription system in _systems)
        {
            system.Dispose();
        }
    }

    /// <summary>
    /// Ends a frame's copy, after the systems ran: takes in what outgrew the slots and finds what is gone. LateUpdate.
    /// </summary>
    internal void Finish()
    {
        _instances.Finish();
        _lights.Finish();
    }

    /// <summary>
    /// Takes what changed in <see cref="Front"/> into the instances' and the lights' rows. <paramref name="time"/> is
    /// the frame's, which a fade starts at; <paramref name="player"/> where detail is wanted first;
    /// <paramref name="isHidden"/> while the world is drawn out of sight. Render thread.
    /// </summary>
    internal void UpdateRows(float time, Vector3 player, bool isHidden)
    {
        Instances.Update(Front, time, player, isHidden);
        Lights.Update(Front);
    }

    /// <summary>
    /// Hands the copy just finished to Render, and takes the one Render is done with to write next. Sync, when neither
    /// thread is touching either.
    /// </summary>
    internal void Swap() => (Front, _back) = (_back, Front);

    private static Vector3 GetIntensity(Color color, float intensity)
        => new Vector3(color.R, color.G, color.B) * intensity;

    /// <summary>
    /// Starts a frame's copy, before the copiers run: the copy Render read two frames ago is written over.
    /// </summary>
    private Renderables Begin()
    {
        _back.Clear();
        _instances.Begin(_back.Instances);
        _lights.Begin(_back.Lights);

        return _back;
    }

    /// <summary>
    /// What the renderer needs that the world has one of, or a few: its cameras, the player, its directional lights,
    /// the sky, the weather and the effects, read in one place, once a frame, after starting the frame's copy.
    /// </summary>
    private sealed class ReadWorld(WorldProcessor owner, World world) : ISystem
    {
        /// <summary>
        /// The radii, in radians, of the sun's and the moon's discs as they look from the Earth, which a sun or moon
        /// light's scale multiplies.
        /// </summary>
        private const float SunRadius = 0.00465f;
        private const float MoonRadius = 0.0045f;

        /// <summary>
        /// How far below the horizon a sun or moon still counts as up, as a sine: half a degree, where the ground
        /// already hides it from the world, so its light has faded to nothing through the air by the time the next is
        /// picked.
        /// </summary>
        private const float Horizon = -0.0087f;

        private bool _hasWarnedOfSuns;
        private bool _hasWarnedOfMoons;
        private bool _hasWarnedOfLights;

        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery query)
        {
            Renderables next = owner.Begin();
            ReadCameras(next.Cameras);
            next.Player = FindTagged<Camera>(Tag.Player)?.Transform.Position;
            ReadDirectionalLights(next);
            next.KeyLight = PickKeyLight(next);
            next.Sky = FindFirst<Sky>()?.Material;
            next.Weather = FindFirst<Weather>();
            next.Effects = FindFirst<PostProcessing>()?.Effects ?? Handle<Pipeline>.None;
            next.Stream = world.Stream;
        }

        /// <summary>
        /// The light that casts the shadows and that the sky's shadow is drawn for: the sun while it is up, else the
        /// moon while it is, else the brightest of the other directional lights, which leaves their list, as it is lit
        /// with its shadows; none without any. A sun or moon that is down lights nothing, so only the one up is lit.
        /// </summary>
        private static KeyLightCopy? PickKeyLight(Renderables next)
        {
            if (next.Sun is { } sun && IsUp(sun.Direction))
                return new KeyLightCopy(sun.Direction, sun.Illuminance, sun.AngularRadius, IsSkyLight: true);

            if (next.Moon is { } moon && IsUp(moon.Direction))
            {
                float share = next.Sun is { } lighting ? GetMoonPhaseShare(lighting.Direction, moon.Direction) : 1;

                return new KeyLightCopy(moon.Direction, moon.Illuminance * share, moon.AngularRadius, IsSkyLight: true);
            }

            int brightest = -1;
            float most = 0;
            for (int i = 0; i < next.Directional.Count; i++)
            {
                Vector3 illuminance = next.Directional[i].Illuminance;
                float lux = illuminance.X + illuminance.Y + illuminance.Z;

                if (lux <= most) continue;

                brightest = i;
                most = lux;
            }

            if (brightest < 0) return null;

            DirectionalCopy light = next.Directional[brightest];
            next.Directional.RemoveAt(brightest);

            return new KeyLightCopy(light.Direction, light.Illuminance, 0, IsSkyLight: false);
        }

        /// <summary>
        /// Whether a sun or moon whose light travels along <paramref name="direction"/> is above the
        /// <see cref="Horizon"/>.
        /// </summary>
        private static bool IsUp(Vector3 direction) => -direction.Y > Horizon;

        /// <summary>
        /// The share of a full moon's light the moon sends the world by its phase, the sun lighting it from an angle
        /// round from behind the Earth: the share of a sphere's lit side that shows (Lambert's sphere), as the sky
        /// draws it.
        /// </summary>
        private static float GetMoonPhaseShare(Vector3 sunDirection, Vector3 moonDirection)
        {
            float cosAngle = Math.Clamp(Vector3.Dot(-sunDirection, moonDirection), -1, 1);
            float angle = MathF.Acos(cosAngle);

            return Math.Clamp((MathF.Sin(angle) + ((MathF.PI - angle) * cosAngle)) / MathF.PI, 0, 1);
        }

        private static void WarnOnce(ref bool hasWarned, string message)
        {
            if (hasWarned) return;

            Debugging.Warn(message);
            hasWarned = true;
        }

        /// <summary>
        /// Every directional light, by its mode: the first sun and the first moon are the sky's, each with a disc as
        /// wide as its entity's largest scale says, and the rest light the world alone, as many as the frame holds. A
        /// second sun or moon, or a light past what the frame holds, lights nothing, which is said once.
        /// </summary>
        private void ReadDirectionalLights(Renderables next)
        {
            foreach ((_, Span<Transform> transforms, Span<DirectionalLight> found) in world
                .Query<Transform, DirectionalLight>())
            {
                for (int i = 0; i < found.Length; i++)
                {
                    Transform transform = transforms[i];
                    DirectionalLight light = found[i];
                    Vector3 direction = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, transform.Rotation));
                    Vector3 illuminance = GetIntensity(light.Color, light.Intensity);
                    float size = MathF.Max(transform.Scale.X, MathF.Max(transform.Scale.Y, transform.Scale.Z));
                    if (light.Mode == DirectionalLightMode.Sun && next.Sun is null)
                    {
                        next.Sun = new SkyLightCopy(direction, illuminance, SunRadius * size);
                    }
                    else if (light.Mode == DirectionalLightMode.Sun)
                    {
                        WarnOnce(
                            ref _hasWarnedOfSuns,
                            "The world has more than one directional light in sun mode; "
                                + "its sky has one sun, the first, and the others light nothing.");
                    }
                    else if (light.Mode == DirectionalLightMode.Moon && next.Moon is null)
                    {
                        next.Moon = new SkyLightCopy(direction, illuminance, MoonRadius * size);
                    }
                    else if (light.Mode == DirectionalLightMode.Moon)
                    {
                        WarnOnce(
                            ref _hasWarnedOfMoons,
                            "The world has more than one directional light in moon mode; "
                                + "its sky has one moon, the first, and the others light nothing.");
                    }
                    else if (next.Directional.Count < DirectionalLights.Capacity)
                    {
                        next.Directional.Add(new DirectionalCopy(direction, illuminance));
                    }
                    else
                    {
                        WarnOnce(
                            ref _hasWarnedOfLights,
                            $"The world has more than {DirectionalLights.Capacity} "
                                + "directional lights besides its sun and moon; a frame lights by that many, and the "
                                + "others light nothing.");
                    }
                }
            }
        }

        private void ReadCameras(List<CameraCopy> cameras)
        {
            foreach ((ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Camera> found) in world
                .Query<Transform, Camera>())
            {
                for (int i = 0; i < found.Length; i++)
                {
                    cameras.Add(new CameraCopy(entities[i], transforms[i], found[i]));
                }
            }
        }

        /// <summary>
        /// The first <typeparamref name="T"/> in the world, or none.
        /// </summary>
        private T? FindFirst<T>()
            where T : unmanaged, IComponent
        {
            foreach ((_, Span<T> found) in world.Query<T>())
            {
                if (found.Length > 0) return found[0];
            }

            return null;
        }

        /// <summary>
        /// The first <typeparamref name="T"/> on an entity tagged <paramref name="tag"/>, with where it is; or none.
        /// </summary>
        private (Transform Transform, T Value)? FindTagged<T>(Tag tag)
            where T : unmanaged, IComponent
        {
            foreach ((_, Span<Transform> transforms, Span<T> found, Span<Tags> tags) in world
                .Query<Transform, T, Tags>())
            {
                for (int i = 0; i < tags.Length; i++)
                {
                    if (tags[i].Has(tag)) return (transforms[i], found[i]);
                }
            }

            return null;
        }
    }

    private sealed class InstanceCopier(Slots<InstanceCopy> instances) : IParallelSystem<Transform, Renderer>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsChunk<Transform, Renderer> chunk)
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Renderer> renderers) = chunk;
            Slots<InstanceCopy>.Tally tally = default;
            for (int i = 0; i < entities.Length; i++)
            {
                instances.Take(entities[i], new InstanceCopy(transforms[i], renderers[i]), ref tally);
            }

            instances.Count(tally);
        }
    }

    private sealed class PointLightCopier(Slots<LightCopy> lights) : IParallelSystem<Transform, PointLight>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsChunk<Transform, PointLight> chunk)
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<PointLight> found) = chunk;
            Slots<LightCopy>.Tally tally = default;
            for (int i = 0; i < found.Length; i++)
            {
                PointLight light = found[i];
                lights.Take(
                    entities[i],
                    new LightCopy(
                        transforms[i].Position,
                        Vector3.UnitZ,
                        GetIntensity(light.Color, light.Intensity),
                        light.Range,
                        light.Radius,
                        0,
                        1,
                        IsSpot: false,
                        light.Shadowless),
                    ref tally);
            }

            lights.Count(tally);
        }
    }

    private sealed class SpotLightCopier(Slots<LightCopy> lights) : IParallelSystem<Transform, SpotLight>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsChunk<Transform, SpotLight> chunk)
        {
            (ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<SpotLight> found) = chunk;
            Slots<LightCopy>.Tally tally = default;
            for (int i = 0; i < found.Length; i++)
            {
                SpotLight light = found[i];
                float cosOuter = MathF.Cos(light.OuterAngle * (MathF.PI / 180));
                float cosInner = MathF.Cos(MathF.Min(light.InnerAngle, light.OuterAngle) * (MathF.PI / 180));
                float scale = 1 / MathF.Max(cosInner - cosOuter, 1e-4f);
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, transforms[i].Rotation);
                lights.Take(
                    entities[i],
                    new LightCopy(
                        transforms[i].Position,
                        forward,
                        GetIntensity(light.Color, light.Intensity),
                        light.Range,
                        light.Radius,
                        scale,
                        -cosOuter * scale,
                        IsSpot: true,
                        light.Shadowless),
                    ref tally);
            }

            lights.Count(tally);
        }
    }
}

/// <summary>
/// The world as one frame's Render reads it: what changed among the instances and lights since the copy before, and
/// the cameras, player, directional lights, sky, weather and effects as they were. There are two, written and read in
/// turn (<see cref="WorldProcessor"/>).
/// </summary>
internal sealed class Renderables
{
    internal Delta<InstanceCopy> Instances { get; } = new();

    /// <summary>
    /// The point and spot lights, by their entity's slot.
    /// </summary>
    internal Delta<LightCopy> Lights { get; } = new();

    internal List<CameraCopy> Cameras { get; } = [];

    /// <summary>
    /// Where the first camera tagged player is, which detail is kept nearest to when there is too much; none without
    /// one.
    /// </summary>
    internal Vector3? Player { get; set; }

    /// <summary>
    /// The sky's sun, the first directional light in sun mode, or none.
    /// </summary>
    internal SkyLightCopy? Sun { get; set; }

    /// <summary>
    /// The sky's moon, the first directional light in moon mode, or none.
    /// </summary>
    internal SkyLightCopy? Moon { get; set; }

    /// <summary>
    /// The directional lights that light the world alone, without shadows: those in default mode, but the one that is
    /// the <see cref="KeyLight"/>.
    /// </summary>
    internal List<DirectionalCopy> Directional { get; } = [];

    /// <summary>
    /// The directional light that casts the shadows: the sun while it is up, else the moon while it is, else the
    /// brightest default light; or none.
    /// </summary>
    internal KeyLightCopy? KeyLight { get; set; }

    /// <summary>
    /// The way the key light's light travels; straight down without one, which then gives no light, so the shadows and
    /// the lighting agree on a direction either way.
    /// </summary>
    internal Vector3 KeyLightDirection => KeyLight?.Direction ?? -Vector3.UnitY;

    /// <summary>
    /// The world's sky, if it has one: its material, which may be none.
    /// </summary>
    internal Handle<Material>? Sky { get; set; }

    /// <summary>
    /// The world's weather, or none: still air and a clear sky.
    /// </summary>
    internal Weather? Weather { get; set; }

    /// <summary>
    /// The world's effects, or none.
    /// </summary>
    internal Handle<Pipeline> Effects { get; set; }

    /// <summary>
    /// What streams into the world this frame (Core's <c>World.Stream</c>): what the renderer takes in and holds, and
    /// whether the world is loading, being readied out of sight, or emptied for a new one.
    /// </summary>
    internal WorldStream Stream { get; set; } = WorldStream.None;

    /// <summary>
    /// Whether a domain is opening in place of the world (Core's <c>World.IsLoading</c>): false from the frame the world
    /// is first shown, the first the window shows it in rather than the image kept as it loaded.
    /// </summary>
    internal bool IsLoading => Stream.State is StreamState.Loading or StreamState.Readying;

    /// <summary>
    /// Whether the world is drawn out of sight, being readied, the window holding the image kept as it loaded in its
    /// place.
    /// </summary>
    internal bool IsHolding => Stream.State == StreamState.Readying;

    internal void Clear()
    {
        Instances.Clear();
        Lights.Clear();
        Cameras.Clear();
        Player = null;
        Sun = null;
        Moon = null;
        Directional.Clear();
        KeyLight = null;
        Sky = null;
        Weather = null;
        Effects = Handle<Pipeline>.None;
        Stream = WorldStream.None;
    }
}

/// <summary>
/// What changed among one kind of entity between two copies: the slots whose value is new or changed, with the value,
/// and the slots whose value is gone. Written from the ECS's threads at once (<see cref="Slots{T}"/>), read on the
/// render thread.
/// </summary>
internal sealed class Delta<T>
    where T : unmanaged
{
    private Change[] _changed = new Change[1024];
    private int _changedCount;

    /// <summary>
    /// The slots there are, live or not: how many a table of them needs.
    /// </summary>
    internal int Length { get; private set; }

    internal ReadOnlySpan<Change> Changed => _changed.AsSpan(0, _changedCount);

    internal List<int> Removed { get; } = [];

    internal void Clear()
    {
        _changedCount = 0;
        Removed.Clear();
    }

    /// <summary>
    /// Notes <paramref name="slot"/> as changed to <paramref name="value"/>, from any thread; there is room for every
    /// slot once (<see cref="Reserve"/>).
    /// </summary>
    internal void Add(int slot, in T value)
        => _changed[Interlocked.Increment(ref _changedCount) - 1] = new Change(slot, value);

    /// <summary>
    /// Makes room for <paramref name="slots"/> changes, and says there are that many slots.
    /// </summary>
    internal void Reserve(int slots)
    {
        Length = slots;
        if (slots > _changed.Length) Array.Resize(ref _changed, (int)BitOperations.RoundUpToPowerOf2((uint)slots));
    }

    /// <summary>
    /// A slot's new value.
    /// </summary>
    internal readonly record struct Change(int Slot, T Value);
}

/// <summary>
/// What one kind of entity was the last frame at each entity slot, taken from the ECS's threads at once: each seen this
/// frame is compared with its copy, and noted in the frame's <see cref="Delta{T}"/> as changed when it is new, its
/// entity is a new one in the slot, or it differs. Slots past the end wait for <see cref="Finish"/>, which grows them;
/// fewer seen than were live and new means some are gone, which only then are looked for. Main thread, and the ECS's
/// threads under it.
/// <para>
/// The comparison is of the two values' bytes, in one call: a generated <c>Equals</c> is a call per field, which an
/// unoptimised build does not inline, and this runs for every entity every frame. Equal bytes mean equal fields, so no
/// change is missed; the values are copied whole out of the ECS's storage, which starts zeroed, so their padding
/// matches too, and were it ever not to, the slot would only be passed on once more.
/// </para>
/// </summary>
internal sealed class Slots<T>(Delta<T> into)
    where T : unmanaged
{
    /// <summary>
    /// Entities seen with a slot past the end, while their system ran on the ECS's threads.
    /// </summary>
    private readonly ConcurrentQueue<(Entity Entity, T Value)> _outgrew = new();

    /// <summary>
    /// The frame's delta, which this frame's changes go into.
    /// </summary>
    private Delta<T> _into = into;

    private Mirror[] _mirrors = new Mirror[1024];
    private int _seenCount;
    private int _liveCount;
    private int _newCount;
    private uint _stamp;

    /// <summary>
    /// One seen this frame, on whichever thread its chunk is on; the chunk's counts go into <paramref name="tally"/>,
    /// which <see cref="Count"/> adds once the chunk is done.
    /// </summary>
    internal void Take(Entity entity, in T value, ref Tally tally)
    {
        if (entity.Id >= _mirrors.Length)
        {
            _outgrew.Enqueue((entity, value));

            return;
        }

        tally.Seen++;
        ref Mirror mirror = ref _mirrors[entity.Id];
        mirror.Stamp = _stamp;
        if (!mirror.IsLive) tally.New++;

        if (mirror.IsLive && mirror.Version == entity.Version
            && MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in mirror.Value))
                .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))))
        {
            return;
        }

        mirror.IsLive = true;
        mirror.Version = entity.Version;
        mirror.Value = value;
        _into.Add(entity.Id, value);
    }

    internal void Count(in Tally tally)
    {
        Interlocked.Add(ref _seenCount, tally.Seen);
        Interlocked.Add(ref _newCount, tally.New);
    }

    /// <summary>
    /// Starts a frame's copy, which goes <paramref name="into"/> the frame's delta, cleared.
    /// </summary>
    internal void Begin(Delta<T> into)
    {
        unchecked
        {
            _stamp++;
        }

        _into = into;
        _into.Reserve(_mirrors.Length);
        _seenCount = 0;
        _newCount = 0;
    }

    internal void Finish()
    {
        while (_outgrew.TryDequeue(out (Entity Entity, T Value) late))
        {
            if (late.Entity.Id >= _mirrors.Length)
            {
                Array.Resize(ref _mirrors, (int)BitOperations.RoundUpToPowerOf2((uint)late.Entity.Id + 1));
                _into.Reserve(_mirrors.Length);
            }

            Tally tally = default;
            Take(late.Entity, late.Value, ref tally);
            Count(tally);
        }

        // Every one seen was live before or is new, so fewer seen than that means some are gone.
        if (_seenCount != _liveCount + _newCount)
        {
            for (int slot = 0; slot < _mirrors.Length; slot++)
            {
                ref Mirror mirror = ref _mirrors[slot];
                if (mirror.IsLive && mirror.Stamp != _stamp)
                {
                    mirror.IsLive = false;
                    _into.Removed.Add(slot);
                }
            }
        }

        _liveCount = _seenCount;
    }

    /// <summary>
    /// How many of a chunk were seen, and of those how many are new.
    /// </summary>
    internal struct Tally
    {
        internal int Seen { get; set; }

        internal int New { get; set; }
    }

    private struct Mirror
    {
        internal T Value;
        internal int Version;
        internal uint Stamp;
        internal bool IsLive;
    }
}
