using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Gpu;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// The scene as the GPU draws it, kept up to date from the <see cref="Snapshot"/>'s changes rather than rebuilt: a row
/// per entity slot (<see cref="GpuSceneInstance"/>) naming its model's row and its material slots' rows, which are made
/// the first time they are asked for and never change as what they name loads, so an instance is written when it
/// changes, once more when what it draws can be drawn, and at no other time. Everything else, which clusters of it to
/// draw and how finely, whether it is seen at all, how far through a fade it is, the GPU works out each frame from these
/// rows. Render thread.
/// <para>
/// An instance's row holds where it was the frame before as well, for velocity: when it changes the old place is kept as
/// that, and the frame after, unless it changed again, its row is written once more with the two the same. A fade is
/// written as the time it started, which the GPU dissolves it by. Transparent surfaces
/// are drawn forward, farthest first, so the instances with any are kept apart for the views to sort
/// (<see cref="Transparent"/>). A model or material no instance uses any more is let go as the frame's changes are taken
/// in, so what is not drawn is not kept.
/// </para>
/// <para>
/// The instances that draw the same model with the same materials share a <see cref="Look"/>, and what follows from it,
/// their material slots' rows, whether any is transparent and why they are drawn as the failure, is worked out once for
/// all of them, so writing an instance costs its transform. A model or material that only comes in changes no row; one
/// that fails is passed on to its looks' instances.
/// </para>
/// <para>
/// Nothing pops in. A look is drawn once it can be drawn as it should be, its model and its cards' atlases in and each
/// of its materials drawn (<see cref="ClusterPool.IsDrawable"/>, <see cref="Materials.IsShown"/>), and from then on;
/// until it is, its instances' rows are empty, so nothing of them is drawn, casts or is in the field global illumination
/// traces. One that waited for it dissolves into view over the shaders' <c>FadeSeconds</c> from when it could be
/// drawn, and what is made from the scene and kept is made again where it stands; one whose look is drawn already
/// appears as it is, as it was made to.
/// </para>
/// </summary>
internal sealed class Scene(IGpu gpu, Assets assets, ClusterPool models, Materials materials) : IDisposable
{
    /// <summary>
    /// Rows changed in a frame past which they are worked out on the thread pool's threads, as a domain arriving changes
    /// tens of thousands at once.
    /// </summary>
    private const int ParallelRows = 4096;

    /// <summary>
    /// The instances drawn as the failure, by slot: why, in a few words, and what failed, by its path.
    /// </summary>
    private readonly Dictionary<int, (string Why, string What)> _failures = [];

    /// <summary>
    /// What the instances drawing each look share, while any does.
    /// </summary>
    private readonly Dictionary<Look, LookEntry> _looks = [];

    /// <summary>
    /// The looks an instance stopped drawing this frame: let go at its end, with their models and materials when no other
    /// look draws them, unless another instance took them up.
    /// </summary>
    private readonly HashSet<Look> _droppedLooks = [];

    /// <summary>
    /// The slots written as moving last frame, whose last place is set to their place this frame unless they move again.
    /// </summary>
    private readonly List<int> _moving = [];
    private readonly List<int> _stillMoving = [];
    private readonly List<MovedSphere> _moved = [];
    private InstanceEntry[] _entries = new InstanceEntry[1024];

    /// <summary>
    /// The slots changed this frame, each with whether its row keeps its last place, and the rows worked out for them,
    /// set together once every change is taken in.
    /// </summary>
    private readonly List<(int Slot, bool KeepsLast)> _changed = [];
    private GpuSceneInstance[] _rows = [];
    private Action<int>? _workOut;

    /// <summary>
    /// The looks with an instance changed this frame, whose model's row is asked for once, as near as the nearest.
    /// </summary>
    private readonly List<LookEntry> _nearer = [];

    /// <summary>
    /// The models and materials some look still draws, gathered when one may be let go (<see cref="GatherDrawn"/>).
    /// </summary>
    private readonly HashSet<Handle<Model>> _drawnModels = [];
    private readonly HashSet<Handle<Material>> _drawnMaterials = [];

    /// <summary>
    /// The models and materials taken in for a domain before it spawned, by how many such holds keep each
    /// (<see cref="Hold"/>): kept though no look draws them yet.
    /// </summary>
    private readonly Dictionary<Handle<Model>, int> _heldModels = [];
    private readonly Dictionary<Handle<Material>, int> _heldMaterials = [];

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
    /// This frame's spheres, each around an instance where it was or where it is, that moved, changed model, came, went,
    /// could be drawn at last, or whose model or a material came in again: where what is made from the scene and kept,
    /// shadows and the field global illumination traces, is made again.
    /// </summary>
    public IReadOnlyList<MovedSphere> Moved => _moved;

    /// <summary>
    /// Where the instance at <paramref name="slot"/> is, as the last frame copied it.
    /// </summary>
    public Vector3 PositionOf(int slot) => _entries[slot].Transform.Position;

    /// <summary>
    /// Keeps the models and materials of <paramref name="preloaded"/>, taken in for a domain before it spawns, though
    /// no look draws them yet, until <see cref="Release"/> lets them go as many times.
    /// </summary>
    public void Hold(Preloaded preloaded)
    {
        foreach (Handle<Model> model in preloaded.Models)
        {
            _heldModels[model] = _heldModels.GetValueOrDefault(model) + 1;
        }

        foreach (Handle<Material> material in preloaded.Materials)
        {
            _heldMaterials[material] = _heldMaterials.GetValueOrDefault(material) + 1;
        }
    }

    /// <summary>
    /// Lets go one hold of <paramref name="preloaded"/>'s models and materials: what no hold keeps and no look draws
    /// leaves the pool and the materials, as a dropped look's does.
    /// </summary>
    public void Release(Preloaded preloaded)
    {
        GatherDrawn();
        foreach (Handle<Model> model in preloaded.Models)
        {
            if (!_heldModels.TryGetValue(model, out int holds))
            {
                continue;
            }

            if (holds > 1)
            {
                _heldModels[model] = holds - 1;
                continue;
            }

            _heldModels.Remove(model);
            if (!_drawnModels.Contains(model))
            {
                models.Release(model);
            }
        }

        foreach (Handle<Material> material in preloaded.Materials)
        {
            if (!_heldMaterials.TryGetValue(material, out int holds))
            {
                continue;
            }

            if (holds > 1)
            {
                _heldMaterials[material] = holds - 1;
                continue;
            }

            _heldMaterials.Remove(material);
            if (!_drawnMaterials.Contains(material))
            {
                materials.Release(material);
            }
        }
    }

    /// <summary>
    /// The model and materials an instance draws.
    /// </summary>
    public Renderer RendererOf(int slot) => _entries[slot].Renderer;

    /// <summary>
    /// Takes in what changed since the last frame, then uploads it. <paramref name="time"/> is the frame's, which a fade
    /// starts at; <paramref name="player"/> where detail is wanted first.
    /// </summary>
    public void Update(WorldCopy copy, float time, Vector3 player)
    {
        Delta<InstanceCopy> instances = copy.Instances;
        if (_entries.Length < instances.Length)
        {
            Array.Resize(ref _entries, instances.Length);
        }

        _moved.Clear();
        _stillMoving.Clear();

        // What moved last frame stands where it was unless it moves again: its last place becomes its place, which a
        // change this frame writes over whole, with the last place the change says.
        foreach (int slot in _moving)
        {
            ref InstanceEntry entry = ref _entries[slot];
            if (entry.IsLive)
            {
                GpuSceneInstance row = Instances.Rows[slot];
                Instances.Set(slot, row with { PrevRow0 = row.Row0, PrevRow1 = row.Row1, PrevRow2 = row.Row2 });
            }
        }

        _moving.Clear();
        foreach (int slot in instances.Removed)
        {
            Remove(slot);
        }

        foreach (ref readonly Delta<InstanceCopy>.Change change in instances.Changed)
        {
            Change(change.Slot, change.Value, time, player);
        }

        WriteChanged();
        _moving.AddRange(_stillMoving);
        Recheck(time);
        LetGo();
        Instances.Flush();
    }

    private void Change(int slot, in InstanceCopy instance, float time, Vector3 player)
    {
        ref InstanceEntry entry = ref _entries[slot];
        Transform transform = instance.Transform;
        Renderer renderer = instance.Renderer;
        bool wasLive = entry.IsLive;
        bool hasMoved = !wasLive || entry.Transform != transform;
        bool isNewLook = !wasLive || entry.Renderer.Model != renderer.Model || entry.Renderer.Materials != renderer.Materials;
        if (wasLive && isNewLook)
        {
            Forget(slot, ref entry);
        }

        if (hasMoved)
        {
            AddMoved(entry, transform, renderer.Model);
        }

        entry.Transform = transform;
        entry.Renderer = renderer;
        entry.IsLive = true;
        entry.FadeStart = wasLive ? entry.FadeStart : time;
        entry.Look = isNewLook ? Take(slot, new Look(renderer.Model, renderer.Materials), time) : entry.Look;
        if (entry.Look is not { } look)
        {
            return;
        }

        float distance = Vector3.Distance(transform.Position, player);
        if (distance < look.Nearest)
        {
            if (look.Nearest == float.MaxValue)
            {
                _nearer.Add(look);
            }

            look.Nearest = distance;
        }

        // A new row's last place is its place already; a moved one's becomes it the frame after, unless it moves again.
        _changed.Add((slot, wasLive && hasMoved));
        if (wasLive && hasMoved)
        {
            _stillMoving.Add(slot);
        }
    }

    /// <summary>
    /// Asks for each changed look's model as near as its nearest changed instance, which orders what the pool takes in
    /// first, then works out every changed row, on the thread pool's threads when there are many, and sets them.
    /// </summary>
    private void WriteChanged()
    {
        foreach (LookEntry look in _nearer)
        {
            look.ModelRow = models.RowOf(look.HasFailed ? models.Failed : look.Look.Model, look.Nearest);
            look.Nearest = float.MaxValue;
        }

        _nearer.Clear();
        if (_rows.Length < _changed.Count)
        {
            Array.Resize(ref _rows, (int)BitOperations.RoundUpToPowerOf2((uint)_changed.Count));
        }

        if (_changed.Count >= ParallelRows)
        {
            Parallel.For(0, _changed.Count, _workOut ??= WorkOut);
        }
        else
        {
            for (int i = 0; i < _changed.Count; i++)
            {
                WorkOut(i);
            }
        }

        for (int i = 0; i < _changed.Count; i++)
        {
            Instances.Set(_changed[i].Slot, _rows[i]);
        }

        _changed.Clear();
    }

    /// <summary>
    /// Works out the <paramref name="index"/>-th changed row. It only reads, so it runs on any thread.
    /// </summary>
    private void WorkOut(int index)
    {
        (int slot, bool keepsLast) = _changed[index];
        _rows[index] = RowOf(slot, _entries[slot], keepsLast);
    }

    /// <summary>
    /// An instance's row: its transform; where it was, the old row's place when it
    /// moved, else its own; its model's row and its material slots', its look's; and how it fades, in from when its look
    /// could be drawn when it waited for that. Empty while its look cannot be drawn yet.
    /// </summary>
    private GpuSceneInstance RowOf(int slot, in InstanceEntry entry, bool keepsLast)
    {
        if (entry.Look is not { IsReady: true } look)
        {
            return default;
        }

        Transform transform = entry.Transform;
        Matrix4x4 linear = transform.Matrix;
        Vector3 position = transform.Position;
        Vector3 scale = Vector3.Abs(transform.Scale);
        bool isMirrored = transform.Scale.X * transform.Scale.Y * transform.Scale.Z < 0;
        bool hasWaited = look.ReadyAt > entry.FadeStart;
        GpuSceneInstance row = new()
        {
            Row0 = new Vector4(linear.M11, linear.M21, linear.M31, position.X),
            Row1 = new Vector4(linear.M12, linear.M22, linear.M32, position.Y),
            Row2 = new Vector4(linear.M13, linear.M23, linear.M33, position.Z),
            MaxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)),
            Flags = (uint)((isMirrored ? GpuSceneFlags.Mirrored : GpuSceneFlags.None)
                | (hasWaited ? GpuSceneFlags.FadeIn : GpuSceneFlags.None)),
            FadeStart = hasWaited ? look.ReadyAt : entry.FadeStart,
        };

        GpuSceneInstance last = keepsLast ? Instances.Rows[slot] : row;
        row.PrevRow0 = last.Row0;
        row.PrevRow1 = last.Row1;
        row.PrevRow2 = last.Row2;
        row.Model = (uint)look.ModelRow;
        row.Materials = look.Rows;
        return row;
    }

    /// <summary>
    /// The entry of what the instances drawing <paramref name="look"/> share, worked out the first time one does, with
    /// <paramref name="slot"/> now among them and counted as transparent or failed as they are.
    /// </summary>
    private LookEntry Take(int slot, Look look, float time)
    {
        if (!_looks.TryGetValue(look, out LookEntry? entry))
        {
            entry = new LookEntry(look);
            Resolve(entry, time);
            _looks[look] = entry;
        }

        entry.Slots.Add(slot);
        Classify(slot, entry);
        return entry;
    }

    /// <summary>
    /// Works out what a look's instances share from its model and materials as they are now: its model's row and its
    /// material slots', or the failure's when the model failed, whether any is transparent, why they are drawn as the
    /// failure, and whether they can be drawn, from <paramref name="time"/> when they could not before. Once they can,
    /// they are drawn for as long as the look is, as what it names loads again.
    /// </summary>
    private void Resolve(LookEntry entry, float time)
    {
        Look look = entry.Look;
        bool hasFailed = models.HasFailed(look.Model);
        (string Why, string What)? failure = hasFailed ? ("Model does not load", assets.Path(look.Model) ?? string.Empty) : null;
        bool isTransparent = false;
        bool areShown = true;
        MaterialRows rows = default;
        for (int i = 0; i < MaterialSlots.Capacity; i++)
        {
            Handle<Material> material = look.Materials[i];
            int materialRow = materials.RowOf(hasFailed ? materials.Failure(FailureType.Mesh) : material);
            rows[i >> 1] |= (uint)materialRow << ((i & 1) * 16);
            areShown &= materials.IsShown(materialRow);
            isTransparent |= !hasFailed && materials.Get(material) is { Type: MaterialType.Transparent };
            if (!hasFailed && materials.FailureOf(material) is { } why)
            {
                failure ??= (why, assets.Path(material) ?? string.Empty);
            }
        }

        entry.HasFailed = hasFailed;
        entry.ModelRow = models.RowOf(hasFailed ? models.Failed : look.Model, float.MaxValue);

        entry.Rows = rows;
        entry.IsTransparent = isTransparent;
        entry.Failure = failure;
        if (!entry.IsReady && areShown && models.IsDrawable(look.Model))
        {
            entry.IsReady = true;
            entry.ReadyAt = time;
        }
    }

    /// <summary>
    /// Counts <paramref name="slot"/> among the transparent, once its look can be drawn, and the failed, as its look is;
    /// it is in neither before.
    /// </summary>
    private void Classify(int slot, LookEntry look)
    {
        if (look.IsTransparent && look.IsReady)
        {
            Transparent.Add(slot);
        }

        if (look.Failure is { } failure)
        {
            _failures[slot] = failure;
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

        Vector3 centre = transform.Position + Vector3.Transform(new Vector3(bounds.X, bounds.Y, bounds.Z) * transform.Scale, transform.Rotation);
        Vector3 scale = Vector3.Abs(transform.Scale);
        return new MovedSphere(centre, bounds.W * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)));
    }

    /// <summary>
    /// Works out again every look that draws a model or material that came in, failed or changed since the last frame,
    /// the failure model's looks among them when it did, and passes on what changed: its instances' rows when it can be
    /// drawn at last, or its model failed or came back,
    /// which of them are transparent or drawn as the failure when that changed, and spheres about them where shadows and
    /// the field are made again, when they can be drawn at last or what they draw came in again. A model or material
    /// that only came in changes no row of a look already drawn, as its row is the one they named all along. Everything
    /// <see cref="Resolve"/> reads of a model or material counts it changed when it changes, so no other look can differ.
    /// </summary>
    private void Recheck(float time)
    {
        if (models.Changed.Count == 0 && materials.Changed.Count == 0)
        {
            return;
        }

        bool hasFailureChanged = models.Changed.Contains(models.Failed) || materials.Changed.Contains(materials.Failure(FailureType.Mesh));
        foreach (LookEntry entry in _looks.Values)
        {
            // A look whose model failed draws the failure model and its material, so it is worked out again as they come in.
            if (!models.Changed.Contains(entry.Look.Model) && !(entry.HasFailed && hasFailureChanged) && !DrawsAny(entry.Look, materials.Changed))
            {
                continue;
            }

            bool hadFailed = entry.HasFailed;
            bool wasReady = entry.IsReady;
            bool wasTransparent = entry.IsTransparent;
            (string Why, string What)? failure = entry.Failure;
            Resolve(entry, time);
            bool isRowChanged = entry.HasFailed != hadFailed || entry.IsReady != wasReady;
            bool isRemade = entry.IsReady;
            if (!isRowChanged && !isRemade && entry.IsTransparent == wasTransparent && entry.Failure == failure)
            {
                continue;
            }

            foreach (int slot in entry.Slots)
            {
                Transparent.Remove(slot);
                _failures.Remove(slot);
                Classify(slot, entry);
                if (isRowChanged)
                {
                    Instances.Set(slot, RowOf(slot, _entries[slot], keepsLast: false));
                }

                if (isRemade && SphereOf(_entries[slot].Transform, entry.Look.Model) is { } sphere)
                {
                    _moved.Add(sphere);
                }
            }
        }

        models.Changed.Clear();
        materials.Changed.Clear();
    }

    /// <summary>
    /// Whether <paramref name="look"/> draws any of <paramref name="drawn"/> in one of its slots.
    /// </summary>
    private static bool DrawsAny(Look look, HashSet<Handle<Material>> drawn)
    {
        if (drawn.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < MaterialSlots.Capacity; i++)
        {
            if (drawn.Contains(look.Materials[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Lets go of the looks no instance draws any more, and of their models and materials when no other look draws them,
    /// so what is not drawn is not kept: they load again when an instance takes them up again.
    /// </summary>
    private void LetGo()
    {
        if (_droppedLooks.Count == 0)
        {
            return;
        }

        foreach (Look dropped in _droppedLooks)
        {
            if (_looks.TryGetValue(dropped, out LookEntry? entry) && entry.Slots.Count == 0)
            {
                _looks.Remove(dropped);
            }
        }

        GatherDrawn();
        foreach (Look dropped in _droppedLooks)
        {
            if (_looks.ContainsKey(dropped))
            {
                continue;
            }

            if (!_drawnModels.Contains(dropped.Model) && !_heldModels.ContainsKey(dropped.Model))
            {
                models.Release(dropped.Model);
            }

            for (int i = 0; i < MaterialSlots.Capacity; i++)
            {
                Handle<Material> material = dropped.Materials[i];
                if (!_drawnMaterials.Contains(material) && !_heldMaterials.ContainsKey(material))
                {
                    materials.Release(material);
                }
            }
        }

        _droppedLooks.Clear();
    }

    /// <summary>
    /// What the looks left draw, gathered once: a domain going away drops thousands of looks in one frame.
    /// </summary>
    private void GatherDrawn()
    {
        _drawnModels.Clear();
        _drawnMaterials.Clear();
        foreach (Look look in _looks.Keys)
        {
            _drawnModels.Add(look.Model);
            for (int i = 0; i < MaterialSlots.Capacity; i++)
            {
                _drawnMaterials.Add(look.Materials[i]);
            }
        }
    }

    /// <summary>
    /// Takes an instance out of its look, and out of the transparent and the failed.
    /// </summary>
    private void Forget(int slot, ref InstanceEntry entry)
    {
        if (!entry.IsLive || entry.Look is not { } look)
        {
            return;
        }

        _failures.Remove(slot);
        Transparent.Remove(slot);
        look.Slots.Remove(slot);
        if (look.Slots.Count == 0)
        {
            _droppedLooks.Add(look.Look);
        }

        entry.Look = null;
    }

    /// <summary>
    /// What an instance draws, but not how it fades: the instances that share one share their rows' model and materials.
    /// </summary>
    private readonly record struct Look(Handle<Model> Model, MaterialSlots Materials);

    /// <summary>
    /// What the instances drawing a look share: their slots, what <see cref="Resolve"/> worked out for them, and the
    /// nearest of them changed this frame, its model asked for as near (<see cref="WriteChanged"/>).
    /// </summary>
    private sealed class LookEntry(Look look)
    {
        public Look Look { get; } = look;

        public HashSet<int> Slots { get; } = [];

        public int ModelRow { get; set; }

        public float Nearest { get; set; } = float.MaxValue;

        public MaterialRows Rows { get; set; }

        public bool HasFailed { get; set; }

        public bool IsTransparent { get; set; }

        public (string Why, string What)? Failure { get; set; }

        /// <summary>
        /// Whether its instances can be drawn as they should be, and since when, in the frame's time.
        /// </summary>
        public bool IsReady { get; set; }

        public float ReadyAt { get; set; }
    }

    /// <summary>
    /// An instance as the scene holds it: where it is and what it draws, its look's entry, and when its fade started.
    /// </summary>
    private struct InstanceEntry
    {
        public Transform Transform;
        public Renderer Renderer;
        public LookEntry? Look;
        public float FadeStart;
        public bool IsLive;
    }
}

/// <summary>
/// A sphere around an instance where it was or is, where what is made from the scene and kept is made again.
/// </summary>
internal readonly record struct MovedSphere(Vector3 Centre, float Radius);

/// <summary>
/// What the renderer takes in for a domain before it spawns (<see cref="IPreloader"/>): the models and materials its
/// prefabs draw, the sky's among them, and the effects they name, none when they name none.
/// </summary>
internal sealed record Preloaded(HashSet<Handle<Model>> Models, HashSet<Handle<Material>> Materials, Handle<Pipeline> Effects)
{
    /// <summary>
    /// What <paramref name="prefabs"/> draw, from their parts and the parts under them. Any thread.
    /// </summary>
    public static Preloaded Of(IReadOnlyList<Prefab> prefabs)
    {
        HashSet<Handle<Model>> models = [];
        HashSet<Handle<Material>> materials = [];
        Handle<Pipeline> effects = Handle<Pipeline>.None;
        foreach (Prefab prefab in prefabs)
        {
            Add(prefab.Parts);
        }

        return new Preloaded(models, materials, effects);

        void Add(IReadOnlyList<PrefabPart> parts)
        {
            foreach (PrefabPart part in parts)
            {
                foreach (IComponent component in part.Components)
                {
                    switch (component)
                    {
                        case Renderer renderer:
                            if (renderer.Model != Handle<Model>.None)
                            {
                                models.Add(renderer.Model);
                            }

                            for (int i = 0; i < MaterialSlots.Capacity; i++)
                            {
                                Handle<Material> material = renderer.Materials[i];
                                if (material != Handle<Material>.None)
                                {
                                    materials.Add(material);
                                }
                            }

                            break;
                        case Sky { Material: var sky } when sky != Handle<Material>.None:
                            materials.Add(sky);
                            break;
                        case PostProcessing { Effects: var named } when named != Handle<Pipeline>.None:
                            effects = named;
                            break;
                        default:
                            break;
                    }
                }

                Add(part.Children);
            }
        }
    }
}
