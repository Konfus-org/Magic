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
/// The world as Render reads it, copied twice over: on the main thread in LateUpdate, by systems that run on the ECS's
/// threads, into the copy Render is not reading (<see cref="WorldCopy"/>), while the render thread may still be drawing
/// the frame before; the two are swapped in Sync, when both threads hold still, and Render reads <see cref="Front"/>
/// while the main thread is busy with the next frame, which writes only the other. Instances and lights are kept at
/// their entity's slot, and only what changed is passed on (<see cref="Delta{T}"/>), so a frame costs the render thread
/// what moved, not what there is: the copy itself is a comparison per entity, on every thread, counted once per chunk.
/// </summary>
internal sealed class Snapshot : IDisposable
{
    private readonly List<Subscription> _systems = [];
    private readonly Slots<InstanceCopy> _instances;
    private readonly Slots<LightCopy> _lights;

    /// <summary>
    /// The copy LateUpdate writes. Main thread, and the ECS's threads under it.
    /// </summary>
    private WorldCopy _back = new();

    internal Snapshot(World world)
    {
        _instances = new Slots<InstanceCopy>(_back.Instances);
        _lights = new Slots<LightCopy>(_back.Lights);
        _systems.Add(world.Add(new Starter(this)));
        _systems.Add(world.Add(new InstanceCopier(_instances)));
        _systems.Add(world.Add(new PointLightCopier(_lights)));
        _systems.Add(world.Add(new SpotLightCopier(_lights)));
        _systems.Add(world.Add(new CameraCopier(this)));
        _systems.Add(world.Add(new PlayerFinder(this)));
        _systems.Add(world.Add(new SunCopier(this)));
        _systems.Add(world.Add(new SkyCopier(this)));
        _systems.Add(world.Add(new PostProcessingCopier(this)));
    }

    public void Dispose()
    {
        foreach (Subscription system in _systems)
        {
            system.Dispose();
        }
    }

    /// <summary>
    /// The copy Render reads: the frame LateUpdate last finished, swapped in by <see cref="Swap"/>. Render thread.
    /// </summary>
    internal WorldCopy Front { get; private set; } = new();

    /// <summary>
    /// Ends a frame's copy, after the systems ran: takes in what outgrew the slots and finds what is gone. LateUpdate.
    /// </summary>
    internal void Finish()
    {
        _instances.Finish();
        _lights.Finish();
    }

    /// <summary>
    /// Hands the copy just finished to Render, and takes the one Render is done with to write next. Sync, when neither
    /// thread is touching either.
    /// </summary>
    internal void Swap()
    {
        (Front, _back) = (_back, Front);
    }

    /// <summary>
    /// Starts a frame's copy, before the systems run: the copy Render read two frames ago is written over.
    /// </summary>
    private void Begin()
    {
        _back.Clear();
        _instances.Begin(_back.Instances);
        _lights.Begin(_back.Lights);
    }

    private static Vector3 GetIntensity(Color color, float intensity) => new Vector3(color.R, color.G, color.B) * intensity;

    /// <summary>
    /// Starts the frame's copy in LateUpdate, before the systems added after it copy anything.
    /// </summary>
    private sealed class Starter(Snapshot snapshot) : ISystem
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery query)
        {
            snapshot.Begin();
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
                lights.Take(entities[i], new LightCopy(transforms[i].Position, Vector3.UnitZ, GetIntensity(light.Color, light.Intensity), light.Range, light.Radius, 0, 1, IsSpot: false, light.Shadowless), ref tally);
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
                lights.Take(entities[i], new LightCopy(transforms[i].Position, forward, GetIntensity(light.Color, light.Intensity), light.Range, light.Radius, scale, -cosOuter * scale, IsSpot: true, light.Shadowless), ref tally);
            }

            lights.Count(tally);
        }
    }

    private sealed class CameraCopier(Snapshot snapshot) : ISystem<Transform, Camera>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<Transform, Camera> query)
        {
            List<CameraCopy> cameras = snapshot._back.Cameras;
            foreach ((ReadOnlySpan<Entity> entities, Span<Transform> transforms, Span<Camera> found) in query)
            {
                for (int i = 0; i < found.Length; i++)
                {
                    cameras.Add(new CameraCopy(entities[i], transforms[i], found[i]));
                }
            }
        }
    }

    private sealed class PlayerFinder(Snapshot snapshot) : ISystem<Camera, Transform, Tags>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<Camera, Transform, Tags> query)
        {
            WorldCopy copy = snapshot._back;
            foreach ((_, _, Span<Transform> transforms, Span<Tags> tags) in query)
            {
                for (int i = 0; i < tags.Length && copy.Player is null; i++)
                {
                    if (tags[i].Has(Tag.Player)) copy.Player = transforms[i].Position;
                }
            }
        }
    }

    private sealed class SunCopier(Snapshot snapshot) : ISystem<Transform, DirectionalLight>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<Transform, DirectionalLight> query)
        {
            WorldCopy copy = snapshot._back;
            foreach ((_, Span<Transform> transforms, Span<DirectionalLight> lights) in query)
            {
                if (copy.Sun is null && lights.Length > 0)
                {
                    copy.Sun = new SunCopy(Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, transforms[0].Rotation)), GetIntensity(lights[0].Color, lights[0].Intensity));
                }
            }
        }
    }

    private sealed class SkyCopier(Snapshot snapshot) : ISystem<Sky>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<Sky> query)
        {
            WorldCopy copy = snapshot._back;
            foreach ((_, Span<Sky> skies) in query)
            {
                if (copy.Sky is null && skies.Length > 0) copy.Sky = skies[0].Material;
            }
        }
    }

    private sealed class PostProcessingCopier(Snapshot snapshot) : ISystem<PostProcessing>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<PostProcessing> query)
        {
            WorldCopy copy = snapshot._back;
            foreach ((_, Span<PostProcessing> found) in query)
            {
                if (copy.Effects == Handle<Pipeline>.None && found.Length > 0) copy.Effects = found[0].Effects;
            }
        }
    }
}

/// <summary>
/// The world as one frame's Render reads it: what changed among the instances and lights since the copy before, and
/// the cameras, player, sun, sky and effects as they were. There are two, written and read in turn
/// (<see cref="Snapshot"/>).
/// </summary>
internal sealed class WorldCopy
{
    internal Delta<InstanceCopy> Instances { get; } = new();

    /// <summary>
    /// The point and spot lights, by their entity's slot.
    /// </summary>
    internal Delta<LightCopy> Lights { get; } = new();

    internal List<CameraCopy> Cameras { get; } = [];

    /// <summary>
    /// Where the first camera tagged player is, which detail is kept nearest to when there is too much; none without one.
    /// </summary>
    internal Vector3? Player { get; set; }

    /// <summary>
    /// The first directional light, or none.
    /// </summary>
    internal SunCopy? Sun { get; set; }

    /// <summary>
    /// The world's sky, if it has one: its material, which may be none.
    /// </summary>
    internal Handle<Material>? Sky { get; set; }

    /// <summary>
    /// The world's effects, or none.
    /// </summary>
    internal Handle<Pipeline> Effects { get; set; }

    internal void Clear()
    {
        Instances.Clear();
        Lights.Clear();
        Cameras.Clear();
        Player = null;
        Sun = null;
        Sky = null;
        Effects = Handle<Pipeline>.None;
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
    {
        _changed[Interlocked.Increment(ref _changedCount) - 1] = new Change(slot, value);
    }

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

        if (mirror.IsLive && mirror.Version == entity.Version && MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in mirror.Value)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))))
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
internal readonly record struct LightCopy(Vector3 Position, Vector3 Forward, Vector3 Intensity, float Range, float Radius, float SpotScale, float SpotOffset, bool IsSpot, bool Shadowless);

/// <summary>
/// The sun: the way its light travels, and its illuminance in lux times its colour.
/// </summary>
internal readonly record struct SunCopy(Vector3 Direction, Vector3 Illuminance);
