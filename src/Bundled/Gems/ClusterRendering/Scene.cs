using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// The scene as the GPU draws it, kept up to date from the <see cref="Snapshot"/>'s changes rather than rebuilt: a row
/// per entity slot (<see cref="GpuSceneInstance"/>) naming its model's row and its material slots' rows, which are made
/// the first time they are asked for and never change as what they name loads, so an instance is written when it
/// changes and at no other time. Everything else, which clusters of it to draw and how finely, whether it is seen at all,
/// how far through a fade it is, the GPU works out each frame from these rows. Render thread.
/// <para>
/// An instance's row holds where it was the frame before as well, for velocity: when it changes the old place is kept as
/// that, and the frame after, unless it changed again, its row is written once more with the two the same. A fade
/// (<see cref="Renderer.Fade"/>) is written as the time it started, which the GPU dissolves it by. Transparent surfaces
/// are drawn forward, farthest first, so the instances with any are kept apart for the views to sort
/// (<see cref="Transparent"/>). A model or material no instance uses any more is let go as the frame's changes are taken
/// in, so what is not drawn is not kept.
/// </para>
/// </summary>
internal sealed class Scene(IGpu gpu, Assets assets, ClusterPool models, Materials materials) : IDisposable
{
    /// <summary>
    /// The instances drawn as the failure, by slot: why, in a few words, and what failed, by its path.
    /// </summary>
    private readonly Dictionary<int, (string Why, string What)> _failures = [];
    private readonly Dictionary<Handle<Model>, HashSet<int>> _byModel = [];
    private readonly Dictionary<Handle<Material>, HashSet<int>> _byMaterial = [];

    /// <summary>
    /// The models and materials an instance stopped using this frame: let go at its end, unless another took them up.
    /// </summary>
    private readonly HashSet<Handle<Model>> _droppedModels = [];
    private readonly HashSet<Handle<Material>> _droppedMaterials = [];

    /// <summary>
    /// The slots written as moving last frame, whose last place is set to their place this frame unless they move again.
    /// </summary>
    private readonly List<int> _moving = [];
    private readonly List<int> _stillMoving = [];
    private readonly List<MovedSphere> _moved = [];
    private InstanceEntry[] _entries = new InstanceEntry[1024];

    public void Dispose()
    {
        Instances.Dispose();
    }

    public GpuArray<GpuSceneInstance> Instances { get; } = new(gpu, GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, 1024);

    /// <summary>
    /// The slots there are, a GPU thread each where culling runs over the scene.
    /// </summary>
    public int Capacity => Instances.Capacity;

    /// <summary>
    /// The slots of the instances with a transparent surface, which the views sort and draw forward.
    /// </summary>
    public HashSet<int> Transparent { get; } = [];

    /// <summary>
    /// The instances drawn as the failure, by slot: why, in a few words, and what failed, by its path.
    /// </summary>
    public IReadOnlyDictionary<int, (string Why, string What)> Failures => _failures;

    /// <summary>
    /// This frame's spheres, each around an instance where it was or where it is, that moved, changed model, came, went
    /// or whose model came in: where what is made from the scene and kept, shadows and the field global illumination
    /// traces, is made again.
    /// </summary>
    public IReadOnlyList<MovedSphere> Moved => _moved;

    /// <summary>
    /// Where the instance at <paramref name="slot"/> is, as the last frame copied it.
    /// </summary>
    public Vector3D PositionOf(int slot) => _entries[slot].Transform.Position;

    /// <summary>
    /// The model and materials an instance draws.
    /// </summary>
    public Renderer RendererOf(int slot) => _entries[slot].Renderer;

    /// <summary>
    /// Takes in what changed since the last frame, then uploads it. <paramref name="time"/> is the frame's, which a fade
    /// starts at; <paramref name="player"/> where detail is wanted first.
    /// </summary>
    public void Update(Snapshot snapshot, float time, Vector3D player)
    {
        Slots<InstanceCopy> instances = snapshot.Instances;
        if (_entries.Length < instances.Length)
        {
            Array.Resize(ref _entries, instances.Length);
        }

        _moved.Clear();
        _stillMoving.Clear();
        foreach (int slot in snapshot.Instances.Removed)
        {
            Remove(slot);
        }

        foreach (int slot in instances.Changed)
        {
            Change(slot, instances[slot], time, player);
        }

        // What moved last frame and not this one stands where it was: its last place becomes its place.
        foreach (int slot in _moving)
        {
            ref InstanceEntry entry = ref _entries[slot];
            if (entry.IsLive && entry.WrittenFor != time)
            {
                GpuSceneInstance row = Instances.Rows[slot];
                Instances.Set(slot, row with { PrevRow0 = row.Row0, PrevRow1 = row.Row1, PrevRow2 = row.Row2, PrevLow = row.Low });
            }
        }

        _moving.Clear();
        _moving.AddRange(_stillMoving);
        Rebound();
        Recheck();
        LetGo();
        Instances.Flush();
    }

    private void Change(int slot, in InstanceCopy instance, float time, Vector3D player)
    {
        ref InstanceEntry entry = ref _entries[slot];
        Transform transform = instance.Transform;
        Renderer renderer = instance.Renderer;
        bool wasLive = entry.IsLive;
        bool hasMoved = !wasLive || entry.Transform != transform;
        if (wasLive && (entry.Renderer.Model != renderer.Model || entry.Renderer.Materials != renderer.Materials))
        {
            Forget(slot, ref entry);
        }

        if (hasMoved)
        {
            AddMoved(entry, transform, renderer.Model);
        }

        bool isNewFade = !wasLive || entry.Renderer.Fade != renderer.Fade;
        entry.Transform = transform;
        entry.Renderer = renderer;
        entry.IsLive = true;
        entry.WrittenFor = time;
        entry.FadeStart = isNewFade ? time : entry.FadeStart;
        Write(slot, ref entry, (float)(transform.Position - player).Length, wasLive && hasMoved);
        if (hasMoved)
        {
            _stillMoving.Add(slot);
        }
    }

    /// <summary>
    /// Writes an instance's row: its transform, split into high and low floats; where it was, the old row's place when
    /// it moved, else its own; its model's row and its material slots', tracking each; and how it fades.
    /// </summary>
    private void Write(int slot, ref InstanceEntry entry, float distance, bool keepsLast)
    {
        Transform transform = entry.Transform;
        Renderer renderer = entry.Renderer;
        Matrix4x4 linear = transform.RelativeTo(transform.Position);
        Vector3D position = transform.Position;
        Vector3 high = position.ToVector3();
        Vector3 low = (position - high).ToVector3();
        Vector3 scale = Vector3.Abs(transform.Scale);
        bool isMirrored = transform.Scale.X * transform.Scale.Y * transform.Scale.Z < 0;
        GpuSceneInstance row = new()
        {
            Row0 = new Vector4(linear.M11, linear.M21, linear.M31, high.X),
            Row1 = new Vector4(linear.M12, linear.M22, linear.M32, high.Y),
            Row2 = new Vector4(linear.M13, linear.M23, linear.M33, high.Z),
            Low = low,
            MaxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)),
            Flags = (uint)((isMirrored ? GpuSceneFlags.Mirrored : GpuSceneFlags.None)
                | (renderer.Fade == RendererFade.In ? GpuSceneFlags.FadeIn : GpuSceneFlags.None)
                | (renderer.Fade == RendererFade.Out ? GpuSceneFlags.FadeOut : GpuSceneFlags.None)),
            FadeStart = entry.FadeStart,
        };

        GpuSceneInstance last = keepsLast ? Instances.Rows[slot] : row;
        row.PrevRow0 = last.Row0;
        row.PrevRow1 = last.Row1;
        row.PrevRow2 = last.Row2;
        row.PrevLow = last.Low;

        Track(_byModel, renderer.Model, slot);
        bool hasFailed = models.HasFailed(renderer.Model);
        row.Model = (uint)models.RowOf(hasFailed ? models.Failed : renderer.Model, distance);
        (string Why, string What)? failure = hasFailed ? ("Model does not load", assets.Path(renderer.Model) ?? string.Empty) : null;
        bool isTransparent = false;
        for (int i = 0; i < 8; i++)
        {
            Handle<Material> material = MaterialIn(renderer.Materials, i);
            Track(_byMaterial, material, slot);
            int materialRow = hasFailed ? materials.FailedMesh.Row : materials.RowOf(material);
            row.Materials[i >> 1] |= (uint)materialRow << ((i & 1) * 16);
            isTransparent |= !hasFailed && materials.Get(material) is { Type: MaterialType.Transparent };
            if (!hasFailed && materials.FailureOf(material) is { } why)
            {
                failure ??= (why, assets.Path(material) ?? string.Empty);
            }
        }

        Instances.Set(slot, row);
        if (isTransparent)
        {
            Transparent.Add(slot);
        }
        else
        {
            Transparent.Remove(slot);
        }

        if (failure is { } failed)
        {
            _failures[slot] = failed;
        }
        else
        {
            _failures.Remove(slot);
        }
    }

    private void Remove(int slot)
    {
        ref InstanceEntry entry = ref _entries[slot];
        if (entry.IsLive)
        {
            AddMoved(entry, null, default);
        }

        Forget(slot, ref entry);
        entry = default;
        Instances.Set(slot, default);
    }

    /// <summary>
    /// Adds the spheres around an instance where it was and where it is now: none for where it was when it was not
    /// there, or where it is when it is going.
    /// </summary>
    private void AddMoved(in InstanceEntry entry, Transform? now, Handle<Model> model)
    {
        if (entry.IsLive && SphereOf(entry.Transform, entry.Renderer.Model) is { } was)
        {
            _moved.Add(was);
        }

        if (now is { } transform && SphereOf(transform, model) is { } goes)
        {
            _moved.Add(goes);
        }
    }

    private MovedSphere? SphereOf(Transform transform, Handle<Model> model)
    {
        Vector4 bounds = models.BoundsOf(model);
        if (bounds.W <= 0)
        {
            return null;
        }

        Vector3D centre = transform.Position + Vector3D.Transform(new Vector3D(bounds.X, bounds.Y, bounds.Z) * transform.Scale, transform.Rotation);
        Vector3 scale = Vector3.Abs(transform.Scale);
        return new MovedSphere(centre, bounds.W * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)));
    }

    /// <summary>
    /// The instances of each model taken in since the last frame: their shape is new where they stand.
    /// </summary>
    private void Rebound()
    {
        foreach (Handle<Model> model in models.Changed)
        {
            if (!_byModel.TryGetValue(model, out HashSet<int>? slots))
            {
                continue;
            }

            foreach (int slot in slots)
            {
                if (SphereOf(_entries[slot].Transform, model) is { } sphere)
                {
                    _moved.Add(sphere);
                }
            }
        }
    }

    /// <summary>
    /// Writes again the instances whose model failed, or whose material came in or failed, since the last frame: which
    /// are transparent, drawn as the failure, or labelled, follows from them.
    /// </summary>
    private void Recheck()
    {
        foreach (Handle<Model> model in models.Changed)
        {
            Rewrite(_byModel, model);
        }

        models.Changed.Clear();
        foreach (Handle<Material> material in materials.Changed)
        {
            Rewrite(_byMaterial, material);
        }

        materials.Changed.Clear();

        void Rewrite<T>(Dictionary<Handle<T>, HashSet<int>> users, Handle<T> handle)
            where T : IAsset<T>
        {
            if (!users.TryGetValue(handle, out HashSet<int>? slots))
            {
                return;
            }

            foreach (int slot in new List<int>(slots))
            {
                ref InstanceEntry entry = ref _entries[slot];
                Write(slot, ref entry, float.MaxValue, keepsLast: false);
            }
        }
    }

    /// <summary>
    /// Lets go of the models and materials no instance uses any more, so what is not drawn is not kept: they load again
    /// when an instance takes them up again.
    /// </summary>
    private void LetGo()
    {
        foreach (Handle<Model> model in _droppedModels)
        {
            if (_byModel.TryGetValue(model, out HashSet<int>? slots) && slots.Count == 0)
            {
                _byModel.Remove(model);
                models.Release(model);
            }
        }

        foreach (Handle<Material> material in _droppedMaterials)
        {
            if (_byMaterial.TryGetValue(material, out HashSet<int>? slots) && slots.Count == 0)
            {
                _byMaterial.Remove(material);
                materials.Release(material);
            }
        }

        _droppedModels.Clear();
        _droppedMaterials.Clear();
    }

    /// <summary>
    /// Stops tracking what an instance uses, and takes it out of the transparent and the failed.
    /// </summary>
    private void Forget(int slot, ref InstanceEntry entry)
    {
        if (!entry.IsLive)
        {
            return;
        }

        _failures.Remove(slot);
        Transparent.Remove(slot);
        if (Untrack(_byModel, entry.Renderer.Model, slot))
        {
            _droppedModels.Add(entry.Renderer.Model);
        }

        for (int i = 0; i < 8; i++)
        {
            Handle<Material> material = MaterialIn(entry.Renderer.Materials, i);
            if (Untrack(_byMaterial, material, slot))
            {
                _droppedMaterials.Add(material);
            }
        }
    }

    public static Handle<Material> MaterialIn(in MaterialSlots slots, int slot) => slot switch
    {
        0 => slots.Slot0,
        1 => slots.Slot1,
        2 => slots.Slot2,
        3 => slots.Slot3,
        4 => slots.Slot4,
        5 => slots.Slot5,
        6 => slots.Slot6,
        _ => slots.Slot7,
    };

    private static void Track<T>(Dictionary<Handle<T>, HashSet<int>> users, Handle<T> handle, int slot)
        where T : IAsset<T>
    {
        if (!users.TryGetValue(handle, out HashSet<int>? slots))
        {
            slots = [];
            users[handle] = slots;
        }

        slots.Add(slot);
    }

    /// <summary>
    /// Stops <paramref name="slot"/> using <paramref name="handle"/>; true when it was the last that did.
    /// </summary>
    private static bool Untrack<T>(Dictionary<Handle<T>, HashSet<int>> users, Handle<T> handle, int slot)
        where T : IAsset<T>
    {
        return users.TryGetValue(handle, out HashSet<int>? slots) && slots.Remove(slot) && slots.Count == 0;
    }

    /// <summary>
    /// An instance as the scene holds it: where it is and what it draws, when its fade started, and the frame's time it
    /// was last written for.
    /// </summary>
    private struct InstanceEntry
    {
        public Transform Transform;
        public Renderer Renderer;
        public float FadeStart;
        public float WrittenFor;
        public bool IsLive;
    }
}

/// <summary>
/// A sphere around an instance where it was or is, where what is made from the scene and kept is made again.
/// </summary>
internal readonly record struct MovedSphere(Vector3D Centre, float Radius);
