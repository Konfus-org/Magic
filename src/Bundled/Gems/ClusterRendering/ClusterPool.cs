using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// Every model drawn, as clusters on the GPU: its row in <see cref="Models"/>, made the first time it is asked for and
/// kept while it is drawn, so an instance's row never changes as its model comes in; its clusters
/// (<see cref="Clusters"/>), levels (<see cref="Levels"/>) and cards (<see cref="Cards"/>); and its vertices and
/// triangles in one buffer of words every draw reads (<see cref="Geometry"/>), packed small. A model loads and is packed
/// on a worker, then waits to be taken in on the render thread, the nearest to the player first and no more a frame than
/// <see cref="ClusterRenderingSettings.UploadBudget"/>, so a burst of models streaming in never stalls a frame. One
/// whose file changes is packed and taken in again, and one no instance draws any more is let go. A model with no
/// clusters of its own (<see cref="Model.Clusters"/>), as one a gem made, is split into clusters of one level as it is.
/// Render thread.
/// <para>
/// A vertex is five words: its position in 20 bits an axis, steps of its model's grid; its normal and its tangent each
/// folded onto an octahedron (Cigolle et al., "A Survey of Efficient Representations for Independent Unit Vectors",
/// 2014), the tangent's handedness in its top bit; and its uv as two halves. A triangle is one word, a byte for each
/// corner's place among its cluster's vertices.
/// </para>
/// </summary>
internal sealed class ClusterPool : IDisposable
{
    /// <summary>
    /// Words a vertex takes in <see cref="Geometry"/>.
    /// </summary>
    public const int VertexWords = 5;

    /// <summary>
    /// A grid step from the model's origin to its farthest vertex along an axis, as Core's clusters are made on.
    /// </summary>
    private const int PositionSteps = (1 << 19) - 1;

    private readonly IGpu _gpu;
    private readonly Assets _assets;
    private readonly Threads _threads;
    private readonly TexturePools _textures;
    private readonly MeshFields _fields;
    private readonly ClusterRenderingSettings _settings;
    private readonly CancellationToken _stopping;
    private static readonly Counter<long> _uploaded = ClusterRenderingGem.Meter.CreateCounter<long>("magic.gems.cluster_rendering.pool.upload.size", "By", "Bytes of models taken into the pool.");
    private static readonly Gauge<int> _heldModels = ClusterRenderingGem.Meter.CreateGauge<int>("magic.gems.cluster_rendering.pool.models", "{model}", "Models held in the pool.");

    private readonly Dictionary<Handle<Model>, ModelEntry> _models = [];
    private readonly List<ModelEntry> _ready = [];
    private readonly RangeAllocator _words = new();
    private readonly RangeAllocator _clusterRows = new();
    private readonly RangeAllocator _levelRows = new();
    private readonly RangeAllocator _cardRows = new();
    private readonly RangeAllocator _modelRows = new();

    /// <summary>
    /// How many models in the pool show each card atlas: a stand-in's cards are its sources' own.
    /// </summary>
    private readonly Dictionary<Handle<Texture>, int> _atlasUsers = [];
    private int _wordCapacity;

    public ClusterPool(IGpu gpu, Assets assets, Threads threads, TexturePools textures, MeshFields fields, ClusterRenderingSettings settings, CancellationToken stopping)
    {
        _gpu = gpu;
        _assets = assets;
        _threads = threads;
        _textures = textures;
        _fields = fields;
        _settings = settings;
        _stopping = stopping;
        GpuBufferUsage read = GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead;
        Models = new GpuArray<GpuModel>(gpu, read, 256);
        Levels = new GpuArray<GpuLevel>(gpu, read, 1024);
        Clusters = new GpuArray<GpuCluster>(gpu, read, 4096);
        Cards = new GpuArray<GpuCard>(gpu, read, 256);
        _wordCapacity = 1 << 22;
        Geometry = gpu.CreateBuffer(read, (uint)_wordCapacity * sizeof(uint));
        Failed = assets.Failure<Model>(FailureType.Mesh);
    }

    public void Dispose()
    {
        Models.Dispose();
        Levels.Dispose();
        Clusters.Dispose();
        Cards.Dispose();
        _gpu.Release(Geometry);
    }

    public GpuArray<GpuModel> Models { get; }

    public GpuArray<GpuLevel> Levels { get; }

    public GpuArray<GpuCluster> Clusters { get; }

    public GpuArray<GpuCard> Cards { get; }

    /// <summary>
    /// Every model's vertices and triangles, as words.
    /// </summary>
    public GpuBuffer Geometry { get; private set; }

    /// <summary>
    /// The model a renderer whose model does not load is drawn with, in the mesh failure material: the failure cube
    /// (<see cref="Assets.Failure{T}"/>).
    /// </summary>
    public Handle<Model> Failed { get; }

    /// <summary>
    /// The models taken in, again or for the first time, failed, or whose cards' atlases or field came in, since the scene
    /// last asked: whose instances can be drawn now, or are drawn anew.
    /// </summary>
    public HashSet<Handle<Model>> Changed { get; } = [];

    /// <summary>
    /// The model's row, made and its load started the first time it is asked for; <paramref name="distance"/> is how far
    /// from the player an instance of it is, which orders what is taken in.
    /// </summary>
    public int RowOf(Handle<Model> model, float distance)
    {
        if (!_models.TryGetValue(model, out ModelEntry? entry))
        {
            entry = new ModelEntry(model, _modelRows.Allocate(1));
            _models[model] = entry;
            Models.Set(entry.Row, default);
            LoadAsync(entry).FireAndForget();
        }

        entry.Distance = MathF.Min(entry.Distance, distance);
        return entry.Row;
    }

    public bool HasFailed(Handle<Model> model) => _models.TryGetValue(model, out ModelEntry? entry) && entry.HasFailed;

    /// <summary>
    /// Whether the model can be drawn as it should be: it is in, and its cards' atlases are in, or one failed and its
    /// clusters stand in for its cards at every distance, so nothing about it changes once it shows; the failure model
    /// in its place, when the model did not load.
    /// </summary>
    public bool IsDrawable(Handle<Model> model) => _models.TryGetValue(model, out ModelEntry? entry) && (entry.HasFailed && model != Failed ? IsDrawable(Failed) : CanDraw(entry));

    /// <summary>
    /// The sphere around the model at full detail, in its own space; none until it is in.
    /// </summary>
    public Vector4 BoundsOf(Handle<Model> model) => _models.TryGetValue(model, out ModelEntry? entry) && entry.In is { } packed ? packed.Bounds : default;

    /// <summary>
    /// The model as it is in the pool, for what draws its clusters itself: its first cluster's row and what was packed;
    /// none until it is in.
    /// </summary>
    public (int FirstCluster, PackedModel Packed)? InPool(Handle<Model> model) => _models.TryGetValue(model, out ModelEntry? entry) && entry.In is { } packed ? (entry.FirstCluster, packed) : null;

    /// <summary>
    /// Lets the model go once no instance draws it: its clusters leave the pool and its row is freed, and it loads again
    /// if it is asked for again. The failure model stays.
    /// </summary>
    public void Release(Handle<Model> model)
    {
        if (model == Failed || !_models.Remove(model, out ModelEntry? entry))
        {
            return;
        }

        _ready.Remove(entry);
        Free(entry);
        Models.Set(entry.Row, default);
        _modelRows.Free(entry.Row, 1);
        _fields.Release(model);
    }

    /// <summary>
    /// The model's file changed and it loaded again: it is packed again, and taken in in place of what it had.
    /// </summary>
    public void Reload(Model model)
    {
        if (_models.TryGetValue(model.Handle, out ModelEntry? entry))
        {
            PackAsync(entry, model).FireAndForget();
        }
    }

    /// <summary>
    /// Takes in the models packed since, the nearest to the player first, as many as the frame's budget allows (always
    /// one, however big); writes cards whose atlases came in, and the rows of the models whose cards are all in now,
    /// counting changed every model that can or cannot be drawn where it could not or could before; then uploads what
    /// changed.
    /// </summary>
    public void Flush()
    {
        long budget = (long)(Math.Max(_settings.UploadBudget, 0.25f) * 1024 * 1024);
        _ready.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int taken = 0;
        long sent = 0;
        while (taken < _ready.Count && (taken == 0 || sent < budget))
        {
            ModelEntry entry = _ready[taken++];
            sent += Take(entry);
        }

        _ready.RemoveRange(0, taken);
        _uploaded.Add(sent);
        _heldModels.Record(_models.Count);
        if (_textures.Changed.Count > 0)
        {
            foreach (ModelEntry entry in _models.Values)
            {
                bool wereIn = entry.AreCardsIn;
                bool wasDrawable = CanDraw(entry);
                WriteCards(entry);
                if (entry.AreCardsIn != wereIn)
                {
                    WriteRow(entry);
                }

                if (entry.AreCardsIn != wereIn || CanDraw(entry) != wasDrawable)
                {
                    Changed.Add(entry.Handle);
                }
            }
        }

        Models.Flush();
        Levels.Flush();
        Clusters.Flush();
        Cards.Flush();
        _fields.Flush();
    }

    private async Task LoadAsync(ModelEntry entry)
    {
        Result<Model> loaded = await _assets.LoadAsync(entry.Handle, _stopping).ConfigureAwait(false);
        if (!loaded.Ok)
        {
            Debugging.Error($"A model does not load, so what draws it is drawn as the failure: {loaded.Error}");
            _threads.Post(EngineThread.Render, () =>
            {
                entry.HasFailed = true;
                Changed.Add(entry.Handle);
            });
            return;
        }

        await PackAsync(entry, loaded.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// Packs the model on a worker, then hands it to the render thread to be taken in in its turn.
    /// </summary>
    private async Task PackAsync(ModelEntry entry, Model model)
    {
        PackedModel packed = await Task.Run(() => Pack(model), _stopping).ConfigureAwait(false);
        _threads.Post(EngineThread.Render, () =>
        {
            if (IsReleased(entry))
            {
                return;
            }

            if (entry.Waiting is null)
            {
                _ready.Add(entry);
            }

            entry.Waiting = packed;
            entry.HasFailed = false;
        });
    }

    /// <summary>
    /// Puts a packed model in the pool in place of what it had: its words, clusters, levels, cards and row, then starts
    /// its field. Answers the bytes it sent.
    /// </summary>
    private long Take(ModelEntry entry)
    {
        if (entry.Waiting is not { } packed)
        {
            return 0;
        }

        Free(entry);
        entry.Waiting = null;
        int words = _words.Allocate(packed.Words.Length);
        if (_words.End > _wordCapacity)
        {
            // Before the model is in: growing uploads again only the models already in, each at its own place.
            Grow(_words.End);
        }

        entry.In = packed;
        _gpu.Upload(Geometry, (uint)words * sizeof(uint), MemoryMarshal.AsBytes(packed.Words.AsSpan()));
        entry.FirstWord = words;
        entry.FirstCluster = _clusterRows.Allocate(packed.Clusters.Length);
        entry.FirstLevel = _levelRows.Allocate(packed.Levels.Length);
        entry.FirstCard = _cardRows.Allocate(packed.Cards.Length);
        for (int i = 0; i < packed.Clusters.Length; i++)
        {
            GpuCluster cluster = packed.Clusters[i];
            cluster.Vertices += (uint)words;
            cluster.Triangles += (uint)words;
            Clusters.Set(entry.FirstCluster + i, cluster);
        }

        for (int i = 0; i < packed.Levels.Length; i++)
        {
            GpuLevel level = packed.Levels[i];
            level.FirstCluster += (uint)entry.FirstCluster;
            Levels.Set(entry.FirstLevel + i, level);
        }

        foreach (PackedCard card in packed.Cards)
        {
            Use(card.Card.Surface.Handle, 1);
            Use(card.Card.Normals.Handle, 1);
        }

        entry.Field = 0;
        WriteCards(entry);
        WriteRow(entry);
        Changed.Add(entry.Handle);
        MakeFieldAsync(entry, packed).FireAndForget();
        return (packed.Words.Length * sizeof(uint)) + (packed.Clusters.Length * GpuArray<GpuCluster>.RowBytes);
    }

    /// <summary>
    /// The model's row: its cards only once their atlases are all in, as a card without its atlas would be drawn a flat
    /// square; until then its clusters are drawn at every distance.
    /// </summary>
    private void WriteRow(ModelEntry entry)
    {
        if (entry.In is not { } packed)
        {
            return;
        }

        Models.Set(entry.Row, new GpuModel
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
            Field = entry.Field,
        });
    }

    /// <summary>
    /// The model's cards with their atlases' refs as they are now, none while an atlas loads, and whether they are all in.
    /// </summary>
    private void WriteCards(ModelEntry entry)
    {
        if (entry.In is not { } packed)
        {
            return;
        }

        bool areIn = true;
        for (int i = 0; i < packed.Cards.Length; i++)
        {
            PackedCard card = packed.Cards[i];
            uint surface = _textures.RefOf(card.Card.Surface.Handle);
            uint normals = _textures.RefOf(card.Card.Normals.Handle);
            areIn &= surface != TexturePools.None && normals != TexturePools.None;
            Cards.Set(entry.FirstCard + i, card.Row with { Surface = surface, Normals = normals });
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
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads or makes the model's field on a worker, from its coarsest clusters, then names it in its row, and counts
    /// the model changed so what is made from its instances' fields is made again.
    /// </summary>
    private async Task MakeFieldAsync(ModelEntry entry, PackedModel packed)
    {
        ModelField? field = await _fields.ReadOrMakeAsync(entry.Handle, packed.Coarsest, _stopping).ConfigureAwait(false);
        _threads.Post(EngineThread.Render, () =>
        {
            if (IsReleased(entry) || entry.In != packed)
            {
                return;
            }

            entry.Field = _fields.Take(entry.Handle, field);
            WriteRow(entry);
            Changed.Add(entry.Handle);
        });
    }

    /// <summary>
    /// Whether the model was let go (<see cref="Release"/>) since <paramref name="entry"/> was its: what a worker made
    /// for it meanwhile is dropped.
    /// </summary>
    private bool IsReleased(ModelEntry entry)
    {
        return !_models.TryGetValue(entry.Handle, out ModelEntry? now) || !ReferenceEquals(now, entry);
    }

    private void Free(ModelEntry entry)
    {
        if (entry.In is not { } packed)
        {
            return;
        }

        _words.Free(entry.FirstWord, packed.Words.Length);
        _clusterRows.Free(entry.FirstCluster, packed.Clusters.Length);
        _levelRows.Free(entry.FirstLevel, packed.Levels.Length);
        _cardRows.Free(entry.FirstCard, packed.Cards.Length);
        foreach (PackedCard card in packed.Cards)
        {
            Use(card.Card.Surface.Handle, -1);
            Use(card.Card.Normals.Handle, -1);
        }

        entry.In = null;
    }

    /// <summary>
    /// Counts a model in the pool showing an atlas, or no longer; the last to let it go lets it leave the pools.
    /// </summary>
    private void Use(Handle<Texture> atlas, int change)
    {
        int users = _atlasUsers.GetValueOrDefault(atlas) + change;
        if (users > 0)
        {
            _atlasUsers[atlas] = users;
            return;
        }

        _atlasUsers.Remove(atlas);
        _textures.Release(atlas);
    }

    /// <summary>
    /// A geometry buffer of at least <paramref name="needed"/> words in place of the old, holding every model's words
    /// again: an upload is copied in the order it is made, so what is uploaded after this lands over it.
    /// </summary>
    private void Grow(int needed)
    {
        _wordCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(needed, _wordCapacity * 2));
        _gpu.Release(Geometry);
        Geometry = _gpu.CreateBuffer(GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, (uint)_wordCapacity * sizeof(uint));
        foreach (ModelEntry entry in _models.Values)
        {
            if (entry.In is { } packed)
            {
                _gpu.Upload(Geometry, (uint)entry.FirstWord * sizeof(uint), MemoryMarshal.AsBytes(packed.Words.AsSpan()));
            }
        }
    }

    /// <summary>
    /// A model packed for the pool: its clusters, or for one with none its meshes split into clusters; its levels; its
    /// cards, which take over from its clusters far off; and the coarsest triangles its field is made from.
    /// </summary>
    private static PackedModel Pack(Model model)
    {
        float step = model.Clusters?.PositionStep ?? StepOver(model.Meshes);
        ClusterHierarchy hierarchy = model.Clusters ?? Flat(model.Meshes, step);
        List<uint> words = [];
        List<GpuCluster> clusters = [];
        List<GpuLevel> levels = [];
        Vector4 bounds = default;
        Span<uint> packedVertex = stackalloc uint[VertexWords];
        Add(hierarchy, packedVertex);
        List<PackedCard> packedCards = [];
        foreach (Mesh mesh in model.Cards?.Meshes ?? [])
        {
            if (mesh.Card is not { } card)
            {
                continue;
            }

            ReadOnlySpan<Vertex> corners = mesh.Vertices.Span;
            for (int i = 0; i + 3 < corners.Length; i += 4)
            {
                Vertex corner = corners[i];
                packedCards.Add(new PackedCard(card, new GpuCard
                {
                    Centre = corner.Position,
                    HalfWidth = corner.Normal.X,
                    HalfHeight = corner.Normal.Y,
                    Turn = corner.Normal.Z,
                    Slot = (uint)mesh.MaterialSlot,
                    Surface = TexturePools.None,
                    Normals = TexturePools.None,
                }));
                float reach = new Vector2(corner.Normal.X, corner.Normal.Y).Length();
                bounds = Enclose(bounds, new Vector4(corner.Position, reach));
            }
        }

        // Each group's error sphere encloses its clusters', so a coarser cluster's, or its parent's, can reach past the
        // bounds: an instance picks its levels of clusters by how near and far all of them reach, or it can pass over
        // the level a cluster's own test wants and draw nothing there.
        Vector3 middle = new(bounds.X, bounds.Y, bounds.Z);
        float clusterReach = bounds.W;
        foreach (GpuCluster cluster in clusters)
        {
            clusterReach = MathF.Max(clusterReach, Vector3.Distance(middle, new Vector3(cluster.ErrorBounds.X, cluster.ErrorBounds.Y, cluster.ErrorBounds.Z)) + cluster.ErrorBounds.W);
            clusterReach = MathF.Max(clusterReach, Vector3.Distance(middle, new Vector3(cluster.ParentErrorBounds.X, cluster.ParentErrorBounds.Y, cluster.ParentErrorBounds.Z)) + cluster.ParentErrorBounds.W);
        }

        return new PackedModel([.. words], [.. clusters], [.. levels], [.. packedCards], bounds, clusterReach, step, model.Cards?.Distance ?? 0, hierarchy.Cut(float.MaxValue));

        // A level is a run of one level's clusters, with the least error and the most parent error of any in it.
        void Add(ClusterHierarchy added, Span<uint> packed)
        {
            ReadOnlySpan<Vertex> vertices = added.Vertices.Span;
            ReadOnlySpan<byte> corners = added.Indices.Span;
            int lastLevel = -1;
            foreach (Cluster cluster in added.Clusters.Span)
            {
                uint firstVertex = (uint)words.Count;
                foreach (Vertex vertex in vertices.Slice(cluster.FirstVertex, cluster.VertexCount))
                {
                    PackVertex(vertex, step, packed);
                    words.AddRange(packed);
                }

                uint firstTriangle = (uint)words.Count;
                for (int t = 0; t < cluster.TriangleCount; t++)
                {
                    int at = cluster.FirstIndex + (t * 3);
                    words.Add(corners[at] | ((uint)corners[at + 1] << 8) | ((uint)corners[at + 2] << 16));
                }

                float parentError = float.IsPositiveInfinity(cluster.ParentError) ? float.MaxValue : cluster.ParentError;
                if (cluster.Level != lastLevel)
                {
                    levels.Add(new GpuLevel { FirstCluster = (uint)clusters.Count, MinError = float.MaxValue });
                    lastLevel = cluster.Level;
                }

                GpuLevel level = levels[^1];
                level.Count++;
                level.MinError = MathF.Min(level.MinError, cluster.Error);
                level.MaxParentError = MathF.Max(level.MaxParentError, parentError);
                levels[^1] = level;
                clusters.Add(new GpuCluster
                {
                    Bounds = cluster.Bounds,
                    ErrorBounds = cluster.ErrorBounds,
                    ParentErrorBounds = cluster.ParentErrorBounds,
                    Error = cluster.Error,
                    ParentError = parentError,
                    Vertices = firstVertex,
                    Triangles = firstTriangle,
                    Counts = GpuCluster.CountsOf(cluster.TriangleCount, cluster.VertexCount, cluster.MaterialSlot, cluster.Level),
                    Step = step,
                });
                if (cluster.Level == 0)
                {
                    bounds = Enclose(bounds, cluster.Bounds);
                }
            }
        }
    }

    /// <summary>
    /// The grid step for <paramref name="meshes"/>: the farthest any vertex is from the origin along an axis, over the
    /// steps a position packs into.
    /// </summary>
    private static float StepOver(IReadOnlyList<Mesh> meshes)
    {
        float farthest = 0;
        foreach (Mesh mesh in meshes)
        {
            foreach (Vertex vertex in mesh.Vertices.Span)
            {
                Vector3 size = Vector3.Abs(vertex.Position);
                farthest = MathF.Max(farthest, MathF.Max(size.X, MathF.Max(size.Y, size.Z)));
            }
        }

        return MathF.Max(farthest, 1e-3f) / PositionSteps;
    }

    /// <summary>
    /// <paramref name="meshes"/> split into clusters as they come, of one level, never simplified: a model drawn as it
    /// is at every distance, on the grid <paramref name="step"/> apart.
    /// </summary>
    private static ClusterHierarchy Flat(IReadOnlyList<Mesh> meshes, float step)
    {
        List<Vertex> vertices = [];
        List<byte> indices = [];
        List<Cluster> clusters = [];
        Dictionary<uint, int> local = [];
        foreach (Mesh mesh in meshes)
        {
            ReadOnlySpan<Vertex> source = mesh.Vertices.Span;
            ReadOnlySpan<uint> triangles = mesh.Indices.Span;
            int firstVertex = vertices.Count;
            int firstIndex = indices.Count;
            local.Clear();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int added = (local.ContainsKey(triangles[t]) ? 0 : 1) + (local.ContainsKey(triangles[t + 1]) ? 0 : 1) + (local.ContainsKey(triangles[t + 2]) ? 0 : 1);
                if ((indices.Count - firstIndex) / 3 == ClusterHierarchy.MaxTriangles || local.Count + added > ClusterHierarchy.MaxVertices)
                {
                    Close();
                }

                for (int corner = 0; corner < 3; corner++)
                {
                    uint index = triangles[t + corner];
                    if (!local.TryGetValue(index, out int at))
                    {
                        at = local.Count;
                        local[index] = at;
                        vertices.Add(source[(int)index]);
                    }

                    indices.Add((byte)at);
                }
            }

            Close();

            void Close()
            {
                int count = (indices.Count - firstIndex) / 3;
                if (count > 0)
                {
                    Vector4 sphere = SphereAround(vertices, firstVertex, vertices.Count - firstVertex);
                    clusters.Add(new Cluster
                    {
                        FirstVertex = firstVertex,
                        VertexCount = vertices.Count - firstVertex,
                        FirstIndex = firstIndex,
                        TriangleCount = count,
                        MaterialSlot = mesh.MaterialSlot,
                        Level = 0,
                        Bounds = sphere,
                        ErrorBounds = sphere,
                        Error = 0,
                        ParentErrorBounds = sphere,
                        ParentError = float.PositiveInfinity,
                    });
                }

                firstVertex = vertices.Count;
                firstIndex = indices.Count;
                local.Clear();
            }
        }

        return new ClusterHierarchy { Vertices = vertices.ToArray(), Indices = indices.ToArray(), Clusters = clusters.ToArray(), PositionStep = step };
    }

    /// <summary>
    /// Packs a vertex into <paramref name="words"/>: its position as steps of <paramref name="step"/>, its normal and
    /// tangent folded onto an octahedron, its uv as halves.
    /// </summary>
    private static void PackVertex(in Vertex vertex, float step, Span<uint> words)
    {
        uint x = Steps(vertex.Position.X);
        uint y = Steps(vertex.Position.Y);
        uint z = Steps(vertex.Position.Z);
        words[0] = x | (y << 20);
        words[1] = (y >> 12) | (z << 8);
        Vector2 normal = Octahedral(vertex.Normal);
        words[2] = Unorm(normal.X, 16) | (Unorm(normal.Y, 16) << 16);
        Vector2 tangent = Octahedral(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z));
        words[3] = Unorm(tangent.X, 16) | (Unorm(tangent.Y, 15) << 16) | (vertex.Tangent.W < 0 ? 1u << 31 : 0);
        words[4] = BitConverter.HalfToUInt16Bits((Half)vertex.Uv.X) | ((uint)BitConverter.HalfToUInt16Bits((Half)vertex.Uv.Y) << 16);

        // Two's complement in 20 bits: a negative step's high bits are cut off, and the shader sign-extends them back.
        uint Steps(float value) => unchecked((uint)(int)Math.Clamp(Math.Round(value / (double)step), -PositionSteps, PositionSteps)) & 0xFFFFF;
    }

    /// <summary>
    /// A direction folded onto an octahedron and unfolded onto a square, 0 to 1 each way (Cigolle et al. 2014).
    /// </summary>
    private static Vector2 Octahedral(Vector3 direction)
    {
        float sum = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);
        if (sum <= 0)
        {
            return new Vector2(0.5f, 0.5f);
        }

        Vector3 folded = direction / sum;
        Vector2 square = new(folded.X, folded.Y);
        if (folded.Z < 0)
        {
            square = new Vector2((1 - MathF.Abs(folded.Y)) * (folded.X >= 0 ? 1 : -1), (1 - MathF.Abs(folded.X)) * (folded.Y >= 0 ? 1 : -1));
        }

        return (square * 0.5f) + new Vector2(0.5f);
    }

    private static uint Unorm(float value, int bits) => (uint)MathF.Round(Math.Clamp(value, 0, 1) * ((1 << bits) - 1));

    private static Vector4 SphereAround(List<Vertex> vertices, int first, int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int i = first; i < first + count; i++)
        {
            min = Vector3.Min(min, vertices[i].Position);
            max = Vector3.Max(max, vertices[i].Position);
        }

        Vector3 centre = (min + max) / 2;
        float radius = 0;
        for (int i = first; i < first + count; i++)
        {
            radius = MathF.Max(radius, Vector3.Distance(centre, vertices[i].Position));
        }

        return new Vector4(centre, radius);
    }

    /// <summary>
    /// The sphere around both; <paramref name="a"/> may be none.
    /// </summary>
    private static Vector4 Enclose(Vector4 a, Vector4 b)
    {
        if (a.W <= 0)
        {
            return b;
        }

        Vector3 from = new(a.X, a.Y, a.Z);
        Vector3 to = new(b.X, b.Y, b.Z);
        float apart = Vector3.Distance(from, to);
        if (apart + b.W <= a.W)
        {
            return a;
        }

        if (apart + a.W <= b.W)
        {
            return b;
        }

        float radius = (apart + a.W + b.W) / 2;
        Vector3 centre = from + ((to - from) * ((radius - a.W) / MathF.Max(apart, 1e-6f)));
        return new Vector4(centre, radius);
    }

    /// <summary>
    /// A model as the pool holds it: its row, what was packed and is in, what is packed and waits its turn, where it is in
    /// the pool, its field's row, whether its cards' atlases are all in, and how near the player the nearest instance
    /// asking for it was.
    /// </summary>
    private sealed class ModelEntry(Handle<Model> handle, int row)
    {
        public Handle<Model> Handle { get; } = handle;

        public int Row { get; } = row;

        public PackedModel? In { get; set; }

        public PackedModel? Waiting { get; set; }

        public int FirstWord { get; set; }

        public int FirstCluster { get; set; }

        public int FirstLevel { get; set; }

        public int FirstCard { get; set; }

        public uint Field { get; set; }

        public bool AreCardsIn { get; set; }

        public float Distance { get; set; } = float.MaxValue;

        public bool HasFailed { get; set; }
    }
}

/// <summary>
/// A model packed for the <see cref="ClusterPool"/>: its words, its clusters with their words counted from its first,
/// its levels with their clusters counted from its first, its cards, its sphere, how far from the sphere's middle its
/// clusters' error spheres reach, its grid's step, the unit-scale distance its cards take over from its clusters (0 for
/// none), and its coarsest triangles.
/// </summary>
internal sealed record PackedModel(uint[] Words, GpuCluster[] Clusters, GpuLevel[] Levels, PackedCard[] Cards, Vector4 Bounds, float ClusterReach, float Step, float CardDistance, IReadOnlyList<Mesh> Coarsest);

/// <summary>
/// A card of a packed model: the card its atlases are read from, and its row but for their refs.
/// </summary>
internal readonly record struct PackedCard(Card Card, GpuCard Row);
