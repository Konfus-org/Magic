using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// A card of a packed model: the card its atlases are read from, and its row but for their refs.
/// </summary>
internal readonly record struct PackedCard(Card Card, GpuCard Row);

/// <summary>
/// The models resident on the GPU, as clusters in one pool. What is kept is what is held: <see cref="Record"/> holds a
/// model, starting its load the first time, and hands back its row, which never changes while it is held, so an
/// instance's row never changes as its model comes in; <see cref="Release"/> lets one hold go, and <see cref="Purge"/>
/// lets go of whatever nothing holds any more. A model is packed on a worker (<see cref="GpuConverter"/>), and placed
/// on the render thread as <see cref="Take"/> runs: its clusters, levels and cards in their tables and its vertices and
/// triangles in one buffer of words every draw reads (<see cref="Geometry"/>), the nearest to the player first and no
/// more a frame than <see cref="VoxelRenderingSettings.ModelUploadRate"/>, so a burst of models streaming in never
/// stalls a frame. A model's cards hold the atlases they show, and are drawn only once both are in. The failure model is
/// held from the start and never leaves. Render thread.
/// </summary>
internal sealed class GpuModels : IDisposable
{
    private static readonly Counter<long> _uploaded = VoxelRenderingGem.Meter.CreateCounter<long>(
        "magic.gems.voxel_rendering.pool.upload.size",
        "By",
        "Bytes of models taken into the pool.");
    private static readonly Gauge<int> _heldCount = VoxelRenderingGem.Meter
        .CreateGauge<int>("magic.gems.voxel_rendering.pool.models", "{model}", "Models held in the pool.");

    private readonly IGpuDriver _gpu;
    private readonly GpuTables _tables;
    private readonly GpuTextures _textures;
    private readonly FailureHandler _failures;
    private readonly VoxelRenderingSettings _settings;
    private readonly Resident<Model, ModelEntry, PackedModel> _models;

    /// <summary>
    /// The models packed and waiting their turn to go in the pool.
    /// </summary>
    private readonly List<ModelEntry> _ready = [];
    private readonly RangeAllocator _words = new();
    private readonly RangeAllocator _clusterRows = new();
    private readonly RangeAllocator _levelRows = new();
    private readonly RangeAllocator _cardRows = new();
    private readonly RangeAllocator _modelRows = new();

    private int _wordCapacity = 1 << 22;

    internal GpuModels(
        IGpuDriver gpu,
        Assets assets,
        GpuTables tables,
        GpuTextures textures,
        FailureHandler failures,
        VoxelRenderingSettings settings,
        CancellationToken stopping)
    {
        _gpu = gpu;
        _tables = tables;
        _textures = textures;
        _failures = failures;
        _settings = settings;
        _models = new(
            assets,
            "A model does not load, so what draws it is drawn as the failure",
            model => Task.Run(() => GpuConverter.Convert(model), stopping),
            stopping);
        Geometry = gpu.CreateBuffer(GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, WordBytes);
        Record(failures.Model);
    }

    /// <summary>
    /// The models taken in, again or for the first time, failed, or whose cards' atlases came in, since the instances
    /// last asked: whose instances can be drawn now, or are drawn anew.
    /// </summary>
    internal HashSet<Handle<Model>> Changed { get; } = [];

    /// <summary>
    /// Every model's vertices and triangles, as words. Its handle changes as it grows.
    /// </summary>
    internal GpuBuffer Geometry { get; private set; }

    private uint WordBytes => (uint)_wordCapacity * sizeof(uint);

    public void Dispose() => _gpu.Release(Geometry);

    /// <summary>
    /// Holds <paramref name="model"/>, starting its load the first time: its row, which never changes while it is held.
    /// </summary>
    internal int Record(Handle<Model> model)
    {
        return _models.Record(
            model,
            () =>
            {
                ModelEntry entry = new(model, _modelRows.Allocate(1));
                _tables.Models.Set(entry.Row, default);

                return entry;
            }).Row;
    }

    /// <summary>
    /// Lets one hold of <paramref name="model"/> go: once none holds it, <see cref="Purge"/> lets it leave.
    /// </summary>
    internal void Release(Handle<Model> model) => _models.Release(model);

    internal void Purge() => _models.Purge(Evict);

    /// <summary>
    /// Takes in what workers finished since the last frame, puts the models packed in the pool, and writes the cards
    /// whose atlases came in. After the textures are taken in, as the cards are worked out from them.
    /// </summary>
    internal void Take()
    {
        _models.Take(TakeModel);
        PlaceReady();
        if (_textures.Changed.Count > 0) WriteChangedCards();
    }

    /// <summary>
    /// The model's file changed and it loaded again: it is packed again, and taken in in place of what it had.
    /// </summary>
    internal void Reload(Model model) => _models.Reload(model);

    /// <summary>
    /// A held model's row; the failure model's for one nothing holds.
    /// </summary>
    internal int GetRow(Handle<Model> model)
        => (_models.Entries.TryGetValue(model, out ModelEntry? entry) ? entry : _models.Entries[_failures.Model]).Row;

    internal bool HasFailed(Handle<Model> model)
        => _models.Entries.TryGetValue(model, out ModelEntry? entry) && entry.HasFailed;

    /// <summary>
    /// Whether the model can be drawn as it should be: it is in, and its cards' atlases are in, or one failed and its
    /// clusters stand in for its cards at every distance, so nothing about it changes once it shows; the failure model
    /// in its place, when the model did not load.
    /// </summary>
    internal bool IsDrawable(Handle<Model> model)
        => _models.Entries.TryGetValue(model, out ModelEntry? entry)
            && (entry.HasFailed && model != _failures.Model ? IsDrawable(_failures.Model) : CanDraw(entry));

    /// <summary>
    /// Takes the model in no later than one an instance of which is <paramref name="distance"/> from the player: the
    /// nearest are taken in first.
    /// </summary>
    internal void Prioritize(Handle<Model> model, float distance)
    {
        if (!_models.Entries.TryGetValue(model, out ModelEntry? entry)) return;

        entry.Distance = MathF.Min(entry.Distance, distance);
    }

    /// <summary>
    /// The sphere around the model at full detail, in its own space; none until it is in.
    /// </summary>
    internal Vector4 GetBounds(Handle<Model> model)
        => _models.Entries.TryGetValue(model, out ModelEntry? entry) && entry.In is { } packed
            ? packed.Bounds
            : default;

    /// <summary>
    /// A model packed, or that did not load (none): it waits its turn to go in the pool, or what draws it is drawn as
    /// the failure.
    /// </summary>
    private void TakeModel(Handle<Model> model, ModelEntry entry, PackedModel? packed)
    {
        if (packed is null)
        {
            entry.HasFailed = true;
            Changed.Add(model);

            return;
        }

        if (entry.Waiting is null) _ready.Add(entry);

        entry.Waiting = packed;
        entry.HasFailed = false;
    }

    /// <summary>
    /// Puts the models packed in the pool, the nearest to the player first, as many as the frame's upload rate allows
    /// (always one, however big).
    /// </summary>
    private void PlaceReady()
    {
        long budget = (long)(Math.Max(_settings.ModelUploadRate, 0.25f) * 1024 * 1024);
        _ready.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int taken = 0;
        long sent = 0;
        while (taken < _ready.Count && (taken == 0 || sent < budget))
        {
            sent += Place(_ready[taken++]);
        }

        _ready.RemoveRange(0, taken);
        _uploaded.Add(sent);
        _heldCount.Record(_models.Entries.Count);
    }

    /// <summary>
    /// Writes every model's cards again as atlases came in or failed, and the rows of the models whose cards are all in
    /// now, counting changed every model that can or cannot be drawn where it could not or could before.
    /// </summary>
    private void WriteChangedCards()
    {
        foreach (ModelEntry entry in _models.Entries.Values)
        {
            bool wereIn = entry.AreCardsIn;
            bool wasDrawable = CanDraw(entry);
            WriteCards(entry);
            if (entry.AreCardsIn != wereIn) WriteRow(entry);

            if (entry.AreCardsIn != wereIn || CanDraw(entry) != wasDrawable) Changed.Add(entry.Handle);
        }
    }

    /// <summary>
    /// Puts a packed model in the pool in place of what it had: its words, clusters, levels, cards and row. Answers the
    /// bytes it sent.
    /// </summary>
    private long Place(ModelEntry entry)
    {
        if (entry.Waiting is not { } packed) return 0;

        // Its new cards' atlases are held before the old ones are let go, so an atlas both show never leaves.
        foreach (PackedCard card in packed.Cards)
        {
            _textures.Record(card.Card.Surface.Handle);
            _textures.Record(card.Card.Normals.Handle);
        }

        Free(entry);
        entry.Waiting = null;
        int words = _words.Allocate(packed.Words.Length);

        // Before the model is in: growing uploads again only the models already in, each at its own place.
        if (_words.End > _wordCapacity) GrowGeometry(_words.End);

        entry.In = packed;
        _gpu.Upload(Geometry, (uint)words * sizeof(uint), MemoryMarshal.AsBytes(packed.Words.AsSpan()));
        entry.FirstWord = words;
        WriteClusters(entry, packed);
        WriteCards(entry);
        WriteRow(entry);
        Changed.Add(entry.Handle);

        return (packed.Words.Length * sizeof(uint)) + (packed.Clusters.Length * GpuTable<GpuCluster>.RowBytes);
    }

    /// <summary>
    /// The model's clusters and levels in rows of their own, counted from where its words and first cluster are.
    /// </summary>
    private void WriteClusters(ModelEntry entry, PackedModel packed)
    {
        entry.FirstCluster = _clusterRows.Allocate(packed.Clusters.Length);
        entry.FirstLevel = _levelRows.Allocate(packed.Levels.Length);
        entry.FirstCard = _cardRows.Allocate(packed.Cards.Length);
        for (int i = 0; i < packed.Clusters.Length; i++)
        {
            GpuCluster cluster = packed.Clusters[i];
            cluster.Vertices += (uint)entry.FirstWord;
            cluster.Triangles += (uint)entry.FirstWord;
            _tables.Clusters.Set(entry.FirstCluster + i, cluster);
        }

        for (int i = 0; i < packed.Levels.Length; i++)
        {
            GpuLevel level = packed.Levels[i];
            level.FirstCluster += (uint)entry.FirstCluster;
            _tables.Levels.Set(entry.FirstLevel + i, level);
        }
    }

    /// <summary>
    /// The model's row: its cards only once their atlases are all in, as a card without its atlas would be drawn a flat
    /// square; until then its clusters are drawn at every distance.
    /// </summary>
    private void WriteRow(ModelEntry entry)
    {
        if (entry.In is not { } packed) return;

        _tables.Models.Set(
            entry.Row,
            new GpuModel
            {
                Bounds = packed.Bounds,
                ClusterReach = packed.ClusterReach,
                FirstCluster = (uint)entry.FirstCluster,
                ClusterCount = (uint)packed.Clusters.Length,
                FirstLevel = (uint)entry.FirstLevel,
                LevelCount = (uint)packed.Levels.Length,
                FirstCard = (uint)entry.FirstCard,
                CardCount = entry.AreCardsIn ? (uint)packed.Cards.Length : 0,
                CardDistance = packed.CardDistance,
                PositionStep = packed.Step,
            });
    }

    /// <summary>
    /// The model's cards with their atlases' refs as they are now, none while an atlas loads, and whether they are all
    /// in.
    /// </summary>
    private void WriteCards(ModelEntry entry)
    {
        if (entry.In is not { } packed) return;

        bool areIn = true;
        for (int i = 0; i < packed.Cards.Length; i++)
        {
            PackedCard card = packed.Cards[i];
            uint surface = _textures.GetRef(card.Card.Surface.Handle);
            uint normals = _textures.GetRef(card.Card.Normals.Handle);
            areIn &= surface != GpuTextures.None && normals != GpuTextures.None;
            _tables.Cards.Set(entry.FirstCard + i, card.Row with { Surface = surface, Normals = normals });
        }

        entry.AreCardsIn = areIn;
    }

    private bool CanDraw(ModelEntry entry) => entry.In is { } packed && (entry.AreCardsIn || HasFailedAtlas(packed));

    /// <summary>
    /// Whether an atlas of the model's cards failed, so they are never drawn.
    /// </summary>
    private bool HasFailedAtlas(PackedModel packed)
    {
        foreach (PackedCard card in packed.Cards)
        {
            if (_textures.HasFailed(card.Card.Surface.Handle) || _textures.HasFailed(card.Card.Normals.Handle))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The model leaves: its clusters leave the pool and its row is freed.
    /// </summary>
    private void Evict(Handle<Model> model, ModelEntry entry)
    {
        _ready.Remove(entry);
        Free(entry);
        _tables.Models.Set(entry.Row, default);
        _modelRows.Free(entry.Row, 1);
    }

    /// <summary>
    /// What the model had in the pool is freed, and its cards' atlases let go.
    /// </summary>
    private void Free(ModelEntry entry)
    {
        if (entry.In is not { } packed) return;

        _words.Free(entry.FirstWord, packed.Words.Length);
        _clusterRows.Free(entry.FirstCluster, packed.Clusters.Length);
        _levelRows.Free(entry.FirstLevel, packed.Levels.Length);
        _cardRows.Free(entry.FirstCard, packed.Cards.Length);
        foreach (PackedCard card in packed.Cards)
        {
            _textures.Release(card.Card.Surface.Handle);
            _textures.Release(card.Card.Normals.Handle);
        }

        entry.In = null;
    }

    /// <summary>
    /// A geometry buffer of at least <paramref name="needed"/> words in place of the old, holding every model's words
    /// again: an upload is copied in the order it is made, so what is uploaded after this lands over it.
    /// </summary>
    private void GrowGeometry(int needed)
    {
        _wordCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(needed, _wordCapacity * 2));
        _gpu.Release(Geometry);
        Geometry = _gpu.CreateBuffer(GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, WordBytes);
        foreach (ModelEntry entry in _models.Entries.Values)
        {
            if (entry.In is not { } packed) continue;

            _gpu.Upload(Geometry, (uint)entry.FirstWord * sizeof(uint), MemoryMarshal.AsBytes(packed.Words.AsSpan()));
        }
    }

    /// <summary>
    /// A model as the pool holds it: its row, what was packed and is in, what is packed and waits its turn, where it is
    /// in the pool, whether its cards' atlases are all in, and how near the player the nearest instance asking for it
    /// was.
    /// </summary>
    private sealed class ModelEntry(Handle<Model> handle, int row)
    {
        internal Handle<Model> Handle { get; } = handle;

        internal int Row { get; } = row;

        internal PackedModel? In { get; set; }

        internal PackedModel? Waiting { get; set; }

        internal int FirstWord { get; set; }

        internal int FirstCluster { get; set; }

        internal int FirstLevel { get; set; }

        internal int FirstCard { get; set; }

        internal bool AreCardsIn { get; set; }

        internal float Distance { get; set; } = float.MaxValue;

        internal bool HasFailed { get; set; }
    }
}

/// <summary>
/// A model packed for the pool: its words, its clusters with their words counted from its first, its levels with their
/// clusters counted from its first, its cards, its sphere, how far from the sphere's middle its clusters' error spheres
/// reach, its grid's step, and the unit-scale distance its cards take over from its clusters (0 for none).
/// </summary>
internal sealed record PackedModel(
    uint[] Words,
    GpuCluster[] Clusters,
    GpuLevel[] Levels,
    PackedCard[] Cards,
    Vector4 Bounds,
    float ClusterReach,
    float Step,
    float CardDistance);
