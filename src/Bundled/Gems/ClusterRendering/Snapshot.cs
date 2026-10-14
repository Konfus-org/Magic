using System.Collections.Concurrent;
using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// The world as Render reads it: copied on the main thread in LateUpdate, by systems that run on the ECS's threads,
/// while the render thread is idle, and read on the render thread in the Render that follows, while the main thread is
/// busy with the next frame's Update and FixedUpdate, which never touch it. Instances and lights are kept at their
/// entity's slot, and only what changed is passed on (<see cref="Slots{T}.Changed"/>, <see cref="Slots{T}.Removed"/>),
/// so a frame costs the render thread what moved, not what there is: the copy itself is a comparison per entity, on
/// every thread, counted once per chunk.
/// </summary>
internal sealed class Snapshot : IDisposable
{
    private readonly List<Subscription> _systems = [];

    public Snapshot(World world)
    {
        _systems.Add(world.Add(new Starter(this)));
        _systems.Add(world.Add(new InstanceCopier(Instances)));
        _systems.Add(world.Add(new PointLightCopier(Lights)));
        _systems.Add(world.Add(new SpotLightCopier(Lights)));
        _systems.Add(world.Add(new CameraCopier(Cameras)));
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

    public Slots<InstanceCopy> Instances { get; } = new();

    /// <summary>
    /// Every point and spot light by its entity's slot.
    /// </summary>
    public Slots<LightCopy> Lights { get; } = new();

    public List<CameraCopy> Cameras { get; } = [];

    /// <summary>
    /// Where the first camera tagged player is, which detail is kept nearest to when there is too much; none without one.
    /// </summary>
    public Vector3D? Player { get; private set; }

    /// <summary>
    /// The first directional light, or none.
    /// </summary>
    public SunCopy? Sun { get; private set; }

    /// <summary>
    /// The world's sky, if it has one: its material, which may be none.
    /// </summary>
    public Handle<Material>? Sky { get; private set; }

    /// <summary>
    /// The world's effects, or none.
    /// </summary>
    public Handle<Pipeline> Effects { get; private set; }

    /// <summary>
    /// Ends a frame's copy, after the systems ran: takes in what outgrew the slots and finds what is gone.
    /// </summary>
    public void Finish()
    {
        Instances.Finish();
        Lights.Finish();
    }

    /// <summary>
    /// Starts a frame's copy, before the systems run: what Render read of the last is done with.
    /// </summary>
    private void Begin()
    {
        Instances.Begin();
        Lights.Begin();
        Player = null;
        Sun = null;
        Sky = null;
        Effects = Handle<Pipeline>.None;
    }

    private static Vector3 IntensityOf(Color color, float intensity) => new Vector3(color.R, color.G, color.B) * intensity;

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
                lights.Take(entities[i], new LightCopy(transforms[i].Position, Vector3.UnitZ, IntensityOf(light.Color, light.Intensity), light.Range, light.Radius, 0, 1, IsSpot: false, light.Shadowless), ref tally);
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
                lights.Take(entities[i], new LightCopy(transforms[i].Position, forward, IntensityOf(light.Color, light.Intensity), light.Range, light.Radius, scale, -cosOuter * scale, IsSpot: true, light.Shadowless), ref tally);
            }

            lights.Count(tally);
        }
    }

    private sealed class CameraCopier(List<CameraCopy> cameras) : ISystem<Transform, Camera>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<Transform, Camera> query)
        {
            cameras.Clear();
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
            foreach ((_, _, Span<Transform> transforms, Span<Tags> tags) in query)
            {
                for (int i = 0; i < tags.Length && snapshot.Player is null; i++)
                {
                    if (tags[i].Has(Tag.Player))
                    {
                        snapshot.Player = transforms[i].Position;
                    }
                }
            }
        }
    }

    private sealed class SunCopier(Snapshot snapshot) : ISystem<Transform, DirectionalLight>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<Transform, DirectionalLight> query)
        {
            foreach ((_, Span<Transform> transforms, Span<DirectionalLight> lights) in query)
            {
                if (snapshot.Sun is null && lights.Length > 0)
                {
                    snapshot.Sun = new SunCopy(Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, transforms[0].Rotation)), IntensityOf(lights[0].Color, lights[0].Intensity));
                }
            }
        }
    }

    private sealed class SkyCopier(Snapshot snapshot) : ISystem<Sky>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<Sky> query)
        {
            foreach ((_, Span<Sky> skies) in query)
            {
                if (snapshot.Sky is null && skies.Length > 0)
                {
                    snapshot.Sky = skies[0].Material;
                }
            }
        }
    }

    private sealed class PostProcessingCopier(Snapshot snapshot) : ISystem<PostProcessing>
    {
        public Phase Phase => Phase.LateUpdate;

        public void Run(in Frame frame, EcsQuery<PostProcessing> query)
        {
            foreach ((_, Span<PostProcessing> found) in query)
            {
                if (snapshot.Effects == Handle<Pipeline>.None && found.Length > 0)
                {
                    snapshot.Effects = found[0].Effects;
                }
            }
        }
    }
}

/// <summary>
/// What one kind of entity was the last frame at each entity slot, taken from the ECS's threads at once: each seen this
/// frame is compared with its copy, and noted as changed when it is new, its entity is a new one in the slot, or it
/// differs. Slots past the end wait for <see cref="Finish"/>, which grows them; fewer seen than were live and new means
/// some are gone, which only then are looked for.
/// </summary>
internal sealed class Slots<T>
    where T : struct, IEquatable<T>
{
    /// <summary>
    /// Entities seen with a slot past the end, while their system ran on the ECS's threads.
    /// </summary>
    private readonly ConcurrentQueue<(Entity Entity, T Value)> _outgrew = new();

    private Mirror[] _mirrors = new Mirror[1024];
    private int[] _changed = new int[1024];
    private int _changedCount;
    private int _seenCount;
    private int _liveCount;
    private int _newCount;
    private uint _stamp;

    /// <summary>
    /// The slots there are, live or not.
    /// </summary>
    public int Length => _mirrors.Length;

    /// <summary>
    /// The slots whose value is new or changed since the last frame.
    /// </summary>
    public ReadOnlySpan<int> Changed => _changed.AsSpan(0, _changedCount);

    /// <summary>
    /// The slots whose value is gone since the last frame.
    /// </summary>
    public List<int> Removed { get; } = [];

    public ref readonly T this[int slot] => ref _mirrors[slot].Value;

    public bool IsLive(int slot) => _mirrors[slot].IsLive;

    /// <summary>
    /// One seen this frame, on whichever thread its chunk is on; the chunk's counts go into <paramref name="tally"/>,
    /// which <see cref="Count"/> adds once the chunk is done.
    /// </summary>
    public void Take(Entity entity, in T value, ref Tally tally)
    {
        if (entity.Id >= _mirrors.Length)
        {
            _outgrew.Enqueue((entity, value));
            return;
        }

        tally.Seen++;
        ref Mirror mirror = ref _mirrors[entity.Id];
        mirror.Stamp = _stamp;
        if (!mirror.IsLive)
        {
            tally.New++;
        }

        if (mirror.IsLive && mirror.Version == entity.Version && mirror.Value.Equals(value))
        {
            return;
        }

        mirror.IsLive = true;
        mirror.Version = entity.Version;
        mirror.Value = value;
        _changed[Interlocked.Increment(ref _changedCount) - 1] = entity.Id;
    }

    public void Count(in Tally tally)
    {
        Interlocked.Add(ref _seenCount, tally.Seen);
        Interlocked.Add(ref _newCount, tally.New);
    }

    public void Begin()
    {
        unchecked
        {
            _stamp++;
        }

        _changedCount = 0;
        _seenCount = 0;
        _newCount = 0;
        Removed.Clear();
    }

    public void Finish()
    {
        while (_outgrew.TryDequeue(out (Entity Entity, T Value) late))
        {
            Grow(ref _mirrors, late.Entity.Id + 1);
            Grow(ref _changed, _mirrors.Length);
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
                    Removed.Add(slot);
                }
            }
        }

        _liveCount = _seenCount;
    }

    private static void Grow<TItem>(ref TItem[] array, int needed)
    {
        if (needed > array.Length)
        {
            Array.Resize(ref array, (int)BitOperations.RoundUpToPowerOf2((uint)needed));
        }
    }

    /// <summary>
    /// How many of a chunk were seen, and of those how many are new.
    /// </summary>
    public struct Tally
    {
        public int Seen { get; set; }

        public int New { get; set; }
    }

    private struct Mirror
    {
        public T Value;
        public int Version;
        public uint Stamp;
        public bool IsLive;
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
internal readonly record struct LightCopy(Vector3D Position, Vector3 Forward, Vector3 Intensity, float Range, float Radius, float SpotScale, float SpotOffset, bool IsSpot, bool Shadowless);

/// <summary>
/// The sun: the way its light travels, and its illuminance in lux times its colour.
/// </summary>
internal readonly record struct SunCopy(Vector3 Direction, Vector3 Illuminance);
