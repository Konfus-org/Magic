using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// The world's instances as the GPU draws them, kept up to date from the copy's changes rather than rebuilt: a row per
/// entity slot (<see cref="GpuInstance"/>) naming its model's row and its material slots' rows, which are made the
/// first time they are asked for and never change as what they name loads, so an instance is written when it changes,
/// once more when what it draws can be drawn, and at no other time. Everything else, which clusters of it to draw and
/// how finely, whether it is seen at all, how far through a fade it is, the GPU works out each frame from these rows.
/// Render thread.
/// <para>
/// An instance's row holds where it was the frame before as well, for velocity: when it changes the old place is kept
/// as that, and the frame after, unless it changed again, its row is written once more with the two the same. A fade is
/// written as the time it started, which the GPU dissolves it by. A look holds its model and materials while an
/// instance draws it and lets them go when none does, so what is not drawn is not kept.
/// </para>
/// <para>
/// The instances that draw the same model with the same materials share a <see cref="Look"/>, and what follows from it,
/// their material slots' rows and why they are drawn as the failure, is worked out once for all of them, so writing an
/// instance costs its transform. A model or material that only comes in changes no row; one that fails is passed on to
/// its looks' instances.
/// </para>
/// <para>
/// Nothing pops in. A look is drawn once it can be drawn as it should be, its model and its cards' atlases in and each
/// of its materials drawn (<see cref="GpuModels.IsDrawable"/>, <see cref="GpuMaterials.IsShown"/>), and from then on;
/// until it is, its instances' rows are empty, so nothing of them is drawn. One that waited for it dissolves into view
/// over the shaders' <c>FadeSeconds</c> from when it could be drawn; one whose look is drawn already appears as it is,
/// as it was made to.
/// </para>
/// </summary>
internal sealed class Instances(
    GpuTables tables,
    GpuModels models,
    GpuMaterials materials,
    FailureHandler failures,
    Assets assets)
{
    /// <summary>
    /// Rows changed in a frame past which they are worked out on the thread pool's threads, as a domain arriving
    /// changes tens of thousands at once.
    /// </summary>
    private const int ParallelRows = 4096;

    /// <summary>
    /// What the instances drawing each look share, while any does.
    /// </summary>
    private readonly Dictionary<Look, LookEntry> _looks = [];

    /// <summary>
    /// The looks an instance stopped drawing this frame: let go at its end, with their holds on their models and
    /// materials, unless another instance took them up.
    /// </summary>
    private readonly HashSet<Look> _droppedLooks = [];

    /// <summary>
    /// The slots written as moving last frame, whose last place is set to their place this frame unless they move
    /// again.
    /// </summary>
    private readonly List<int> _moving = [];
    private readonly List<int> _stillMoving = [];

    /// <summary>
    /// The slots changed this frame, each with whether its row keeps its last place, and the rows worked out for them,
    /// set together once every change is taken in.
    /// </summary>
    private readonly List<(int Slot, bool KeepsLast)> _changed = [];

    /// <summary>
    /// The looks with an instance changed this frame, whose model is asked for once, as near as the nearest.
    /// </summary>
    private readonly List<LookEntry> _nearer = [];

    private GpuInstance[] _rows = [];
    private InstanceEntry[] _entries = new InstanceEntry[1024];
    private Action<int>? _workOut;

    /// <summary>
    /// Whether this frame's world is drawn out of sight, as it is readied: a look that can be drawn at last then shows
    /// at once rather than dissolving in, as nobody sees it come in.
    /// </summary>
    private bool _isHidden;

    /// <summary>
    /// The slots there are, a GPU thread each where culling runs over the instances.
    /// </summary>
    internal int Capacity => tables.Instances.Capacity;

    /// <summary>
    /// Where the instance at <paramref name="slot"/> is, as the last frame copied it.
    /// </summary>
    internal Vector3 GetPosition(int slot) => _entries[slot].Transform.Position;

    /// <summary>
    /// Whether an instance waits for its look to be drawn at last: its model, cards or a material not in yet.
    /// </summary>
    internal bool HasWaiting()
    {
        foreach (LookEntry entry in _looks.Values)
        {
            if (!entry.IsReady) return true;
        }

        return false;
    }

    /// <summary>
    /// Takes in what changed since the last frame into the instances' table. <paramref name="time"/> is the frame's,
    /// which a fade starts at; <paramref name="player"/> where detail is wanted first; <paramref name="isHidden"/>
    /// while the world is drawn out of sight.
    /// </summary>
    internal void Update(Renderables copy, float time, Vector3 player, bool isHidden)
    {
        _isHidden = isHidden;
        Delta<InstanceCopy> instances = copy.Instances;
        if (_entries.Length < instances.Length) Array.Resize(ref _entries, instances.Length);

        _stillMoving.Clear();

        // What moved last frame stands where it was unless it moves again: its last place becomes its place, which a
        // change this frame writes over whole, with the last place the change says.
        foreach (int slot in _moving)
        {
            ref InstanceEntry entry = ref _entries[slot];
            if (entry.IsLive)
            {
                GpuInstance row = tables.Instances.Rows[slot];
                tables.Instances.Set(slot, row with { PrevRow0 = row.Row0, PrevRow1 = row.Row1, PrevRow2 = row.Row2 });
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
    }

    /// <summary>
    /// Whether <paramref name="look"/> draws any of <paramref name="drawn"/> in one of its slots.
    /// </summary>
    private static bool IsDrawingAnyOf(Look look, HashSet<Handle<Material>> drawn)
    {
        if (drawn.Count == 0) return false;

        for (int i = 0; i < MaterialSlots.Capacity; i++)
        {
            if (drawn.Contains(look.Materials[i])) return true;
        }

        return false;
    }

    private void Change(int slot, in InstanceCopy instance, float time, Vector3 player)
    {
        ref InstanceEntry entry = ref _entries[slot];
        Transform transform = instance.Transform;
        Renderer renderer = instance.Renderer;
        bool wasLive = entry.IsLive;
        bool hasMoved = !wasLive || entry.Transform != transform;
        bool isNewLook = !wasLive || entry.Renderer.Model != renderer.Model
            || entry.Renderer.Materials != renderer.Materials;
        if (wasLive && isNewLook) Forget(slot, ref entry);

        entry.Transform = transform;
        entry.Renderer = renderer;
        entry.IsLive = true;
        entry.FadeStart = wasLive ? entry.FadeStart : time;
        entry.Look = isNewLook ? Take(slot, new Look(renderer.Model, renderer.Materials), time) : entry.Look;

        if (entry.Look is not { } look) return;

        float distance = Vector3.Distance(transform.Position, player);
        if (distance < look.Nearest)
        {
            if (look.Nearest == float.MaxValue) _nearer.Add(look);

            look.Nearest = distance;
        }

        // A new row's last place is its place already; a moved one's becomes it the frame after, unless it moves again.
        _changed.Add((slot, wasLive && hasMoved));
        if (wasLive && hasMoved) _stillMoving.Add(slot);
    }

    /// <summary>
    /// Asks for each changed look's model as near as its nearest changed instance, which orders what the pool takes in
    /// first, then works out every changed row, on the thread pool's threads when there are many, and sets them.
    /// </summary>
    private void WriteChanged()
    {
        foreach (LookEntry look in _nearer)
        {
            models.Prioritize(look.HasFailed ? failures.Model : look.Look.Model, look.Nearest);
            look.Nearest = float.MaxValue;
        }

        _nearer.Clear();
        if (_rows.Length < _changed.Count)
            Array.Resize(ref _rows, (int)BitOperations.RoundUpToPowerOf2((uint)_changed.Count));

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
            tables.Instances.Set(_changed[i].Slot, _rows[i]);
        }

        _changed.Clear();
    }

    /// <summary>
    /// Works out the <paramref name="index"/>-th changed row. It only reads, so it runs on any thread.
    /// </summary>
    private void WorkOut(int index)
    {
        (int slot, bool keepsLast) = _changed[index];
        _rows[index] = MakeRow(slot, _entries[slot], keepsLast);
    }

    /// <summary>
    /// An instance's row: its transform; where it was, the old row's place when it moved, else its own; its model's row
    /// and its material slots', its look's; and how it fades, in from when its look could be drawn when it waited for
    /// that. Empty while its look cannot be drawn yet.
    /// </summary>
    private GpuInstance MakeRow(int slot, in InstanceEntry entry, bool keepsLast)
    {
        if (entry.Look is not { IsReady: true } look) return default;

        Transform transform = entry.Transform;
        Matrix4x4 linear = transform.Matrix;
        Vector3 position = transform.Position;
        Vector3 scale = Vector3.Abs(transform.Scale);
        bool isMirrored = transform.Scale.X * transform.Scale.Y * transform.Scale.Z < 0;
        bool hasWaited = look.ReadyAt > entry.FadeStart;
        GpuInstance row = new()
        {
            Row0 = new Vector4(linear.M11, linear.M21, linear.M31, position.X),
            Row1 = new Vector4(linear.M12, linear.M22, linear.M32, position.Y),
            Row2 = new Vector4(linear.M13, linear.M23, linear.M33, position.Z),
            MaxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)),
            Flags = (uint)((isMirrored ? GpuInstanceFlags.Mirrored : GpuInstanceFlags.None)
                | (hasWaited ? GpuInstanceFlags.FadeIn : GpuInstanceFlags.None)),
            FadeStart = hasWaited ? look.ReadyAt : entry.FadeStart,
        };

        GpuInstance last = keepsLast ? tables.Instances.Rows[slot] : row;
        row.PrevRow0 = last.Row0;
        row.PrevRow1 = last.Row1;
        row.PrevRow2 = last.Row2;
        row.Model = (uint)look.ModelRow;
        row.Materials = look.Rows;

        return row;
    }

    /// <summary>
    /// The entry of what the instances drawing <paramref name="look"/> share, worked out the first time one does, its
    /// model and materials held from then, with <paramref name="slot"/> now among them and marked failed as they are.
    /// </summary>
    private LookEntry Take(int slot, Look look, float time)
    {
        if (!_looks.TryGetValue(look, out LookEntry? entry))
        {
            entry = new LookEntry(look);
            Hold(look);
            Resolve(entry, time);
            _looks[look] = entry;
        }

        entry.Slots.Add(slot);
        if (entry.Failure is { } failure) failures.Mark(slot, failure);

        return entry;
    }

    /// <summary>
    /// Works out what a look's instances share from its model and materials as they are now: its model's row and its
    /// material slots', or the failure's when the model failed or a slot names a sky, which no mesh draws, why they are
    /// drawn as the failure, and whether they can be drawn, from <paramref name="time"/> when they could not before.
    /// Once they can, they are drawn for as long as the look is, as what it names loads again.
    /// </summary>
    private void Resolve(LookEntry entry, float time)
    {
        Look look = entry.Look;
        bool hasFailed = models.HasFailed(look.Model);
        (string Why, string What)? failure = hasFailed
            ? ("Model does not load", assets.GetPath(look.Model) ?? string.Empty)
            : null;
        bool areShown = true;
        MaterialRows rows = default;
        for (int i = 0; i < MaterialSlots.Capacity; i++)
        {
            Handle<Material> material = look.Materials[i];
            bool isSky = !hasFailed && materials.IsSky(material);
            Handle<Material> drawn = (hasFailed, isSky) switch
            {
                (true, _) => failures.GetMaterial(FailureType.Mesh),
                (false, true) => failures.GetMaterial(FailureType.Shader),
                _ => material,
            };
            rows[i >> 1] |= (uint)materials.GetRow(drawn) << ((i & 1) * 16);
            areShown &= materials.IsShown(drawn);
            if (isSky) failure ??= ("Sky material on a mesh", assets.GetPath(material) ?? string.Empty);
            else if (!hasFailed && materials.GetFailure(material) is { } why)
                failure ??= (why, assets.GetPath(material) ?? string.Empty);
        }

        entry.HasFailed = hasFailed;
        entry.ModelRow = models.GetRow(hasFailed ? failures.Model : look.Model);
        entry.Rows = rows;
        entry.Failure = failure;

        if (entry.IsReady || !areShown || !models.IsDrawable(look.Model)) return;

        entry.IsReady = true;

        // Before every instance's fade start, so none dissolves in, when nobody sees the look come in.
        entry.ReadyAt = _isHidden ? float.NegativeInfinity : time;
    }

    private void Remove(int slot)
    {
        ref InstanceEntry entry = ref _entries[slot];
        Forget(slot, ref entry);
        entry = default;
        tables.Instances.Set(slot, default);
    }

    /// <summary>
    /// Works out again every look that draws a model or material that came in, failed or changed since the last frame,
    /// the failure model's looks among them when it did, and passes on what changed: its instances' rows when it can be
    /// drawn at last, or its model failed or came back, and which of them are drawn as the failure when that changed. A
    /// model or material that only came in changes no row of a look already drawn, as its row is the one they named all
    /// along. Everything <see cref="Resolve"/> reads of a model or material counts it changed when it changes, so no
    /// other look can differ.
    /// </summary>
    private void Recheck(float time)
    {
        if (models.Changed.Count == 0 && materials.Changed.Count == 0) return;

        bool hasFailureChanged = models.Changed.Contains(failures.Model)
            || materials.Changed.Contains(failures.GetMaterial(FailureType.Mesh));
        foreach (LookEntry entry in _looks.Values)
        {
            // A look whose model failed draws the failure model and its material, so it is worked out again as they
            // come in.
            if (!models.Changed.Contains(entry.Look.Model) && !(entry.HasFailed && hasFailureChanged)
                && !IsDrawingAnyOf(entry.Look, materials.Changed))
            {
                continue;
            }

            bool hadFailed = entry.HasFailed;
            bool wasReady = entry.IsReady;
            (string Why, string What)? failure = entry.Failure;
            Resolve(entry, time);
            bool isRowChanged = entry.HasFailed != hadFailed || entry.IsReady != wasReady;

            if (!isRowChanged && entry.Failure == failure) continue;

            foreach (int slot in entry.Slots)
            {
                failures.Unmark(slot);
                if (entry.Failure is { } now) failures.Mark(slot, now);

                if (isRowChanged) tables.Instances.Set(slot, MakeRow(slot, _entries[slot], keepsLast: false));
            }
        }

        models.Changed.Clear();
        materials.Changed.Clear();
    }

    /// <summary>
    /// Lets go of the looks no instance draws any more, and of their holds on their models and materials, which the
    /// residents let leave once nothing else holds them: they load again when an instance takes them up again.
    /// </summary>
    private void LetGo()
    {
        foreach (Look dropped in _droppedLooks)
        {
            if (!_looks.TryGetValue(dropped, out LookEntry? entry) || entry.Slots.Count > 0) continue;

            _looks.Remove(dropped);
            models.Release(dropped.Model);
            for (int i = 0; i < MaterialSlots.Capacity; i++)
            {
                materials.Release(dropped.Materials[i]);
            }
        }

        _droppedLooks.Clear();
    }

    /// <summary>
    /// Holds a new look's model and materials while an instance draws it.
    /// </summary>
    private void Hold(Look look)
    {
        models.Record(look.Model);
        for (int i = 0; i < MaterialSlots.Capacity; i++)
        {
            materials.Record(look.Materials[i]);
        }
    }

    /// <summary>
    /// Takes an instance out of its look, and out of the failed.
    /// </summary>
    private void Forget(int slot, ref InstanceEntry entry)
    {
        if (!entry.IsLive || entry.Look is not { } look) return;

        failures.Unmark(slot);
        look.Slots.Remove(slot);
        if (look.Slots.Count == 0) _droppedLooks.Add(look.Look);

        entry.Look = null;
    }

    /// <summary>
    /// What an instance draws, but not how it fades: the instances that share one share their rows' model and
    /// materials.
    /// </summary>
    private readonly record struct Look(Handle<Model> Model, MaterialSlots Materials);

    /// <summary>
    /// An instance as these hold it: where it is and what it draws, its look's entry, and when its fade started.
    /// </summary>
    private struct InstanceEntry
    {
        internal Transform Transform;
        internal Renderer Renderer;
        internal LookEntry? Look;
        internal float FadeStart;
        internal bool IsLive;
    }

    /// <summary>
    /// What the instances drawing a look share: their slots, what <see cref="Resolve"/> worked out for them, and the
    /// nearest of them changed this frame, its model asked for as near (<see cref="WriteChanged"/>).
    /// </summary>
    private sealed class LookEntry(Look look)
    {
        internal Look Look { get; } = look;

        internal HashSet<int> Slots { get; } = [];

        internal int ModelRow { get; set; }

        internal float Nearest { get; set; } = float.MaxValue;

        internal MaterialRows Rows { get; set; }

        internal bool HasFailed { get; set; }

        internal (string Why, string What)? Failure { get; set; }

        /// <summary>
        /// Whether its instances can be drawn as they should be, and since when, in the frame's time.
        /// </summary>
        internal bool IsReady { get; set; }

        internal float ReadyAt { get; set; }
    }
}
