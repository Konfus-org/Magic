using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// Where an asset is in the loading: not yet, on the GPU, or never, drawn as the failure.
/// </summary>
internal enum ResourceState : byte
{
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// The GPU resources the gem draws with, each read once on its first ask: a texture asset by its handle, read and asked
/// of the graph with every level and the sampler its asset asks for, then copied up in the frame after it arrives; a
/// material asset by its handle, read once, with the texture it names asked for, and handed out as the material row a
/// slot's params make over its own; a model asset by its handle, made by the preprocessor on a worker and copied into
/// the pools of vertices, indices, clusters and leaf clusters in the frame after it arrives. The pools and the textures
/// are asked of the graph every frame, so the GPU provider keeps what they hold. Each pool hands out ranges first fit
/// from a free list. A model not asked for in <see cref="UnusedSec"/>, and held by no renderer drawn, is evicted: its
/// ranges go back to the free lists and its acceleration structure is destroyed, and the next ask makes it again.
/// </summary>
internal sealed class GpuResources(
    IGraphicsApi gpu,
    ModelPreprocessor models,
    Caches caches,
    AssetManager assets,
    Threads threads
) : IDisposable
{
    // PROTO: raised for the terrain sample's 2M-triangle terrain.
    private const int VertexCapacity = 1 << 22;
    private const int IndexCapacity = 1 << 24;
    private const int ClusterCapacity = 1 << 17;
    private const int ImpostorCapacity = 1024;
    private const string FailureShader = "Shaders/Material/Failure.slang";

    /// <summary>
    /// How long, in seconds of real time, a model stays on the GPU after its last ask.
    /// </summary>
    private const int UnusedSec = 60;

    private readonly Dictionary<ulong, TextureEntry> _textures = [];

    /// <summary>
    /// Each material asked for, by its id, null while it loads; one that did not load is the failure material.
    /// </summary>
    private readonly Dictionary<ulong, Material?> _materials = [];
    private readonly List<(Texture Asset, TextureEntry Entry)> _arrivedTextures = [];

    // PROTO: the impostor table's rows; row 0 is no impostor, so it is taken from the start.
    private readonly FreeList _impostorRows = CreateImpostorRows();

    /// <summary>
    /// Each model asked for and not evicted, by its id, made or not, with when it was last asked, in
    /// <see cref="Environment.TickCount64"/> milliseconds.
    /// </summary>
    private readonly Dictionary<ulong, long> _askedModels = [];
    private readonly Dictionary<ulong, ResidentModel> _residentModels = [];
    private readonly HashSet<ulong> _failedModels = [];
    private readonly List<(MeshDerived Derived, ResidentModel Where)> _arrivedModels = [];
    private readonly FreeList _vertexRanges = new(VertexCapacity);
    private readonly FreeList _indexRanges = new(IndexCapacity);
    private readonly FreeList _clusterRanges = new(ClusterCapacity);
    private readonly FreeList _leafRanges = new(ClusterCapacity);

    /// <summary>
    /// The material row a slot is drawn with until its material is loaded, and an empty slot always.
    /// </summary>
    public static GpuMaterial DefaultMaterial =>
        new() { BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1), Roughness = 0.6f };

    /// <summary>
    /// How many models were made or failed, ever: when it changes, a renderer waiting for its model may draw now.
    /// </summary>
    public int SettledModels { get; private set; }

    public GpuBuffer VertexPool { get; private set; }

    public GpuBuffer IndexPool { get; private set; }

    public GpuBuffer ClusterPool { get; private set; }

    /// <summary>
    /// The leaf clusters of every resident model, in the order its acceleration structure's geometries name them;
    /// none on a GPU that traces no rays.
    /// </summary>
    public GpuBuffer LeafClusters { get; private set; }

    /// <summary>
    /// PROTO: every resident model's impostor, a <see cref="GpuImpostor"/> a row, by the row its instances name.
    /// </summary>
    public GpuBuffer ImpostorTable { get; private set; }

    private static GpuMaterial FailureMaterial =>
        new()
        {
            BaseColor = new Vector4(1, 0, 1, 1),
            Roughness = 1,
            Kind = (uint)MaterialKind.Failure,
        };

    /// <summary>
    /// The textures shaders read through the heap, by the id in a row rather than by a pass's name: every material's
    /// texture on the GPU and every resident model's impostor atlas. A pass that samples them names them in its reads,
    /// so each is readable, its copy done, before the pass runs.
    /// </summary>
    public ImmutableArray<GpuResource> GetHeapTextures()
    {
        ImmutableArray<GpuResource>.Builder textures = ImmutableArray.CreateBuilder<GpuResource>();
        foreach (TextureEntry entry in _textures.Values)
        {
            if (entry.State == ResourceState.Ready)
                textures.Add(entry.Texture);
        }

        foreach (ResidentModel resident in _residentModels.Values)
        {
            if (resident.Impostor.IsNone)
                continue;

            textures.Add(resident.Impostor);
            textures.Add(resident.ImpostorUv);
        }

        return textures.ToImmutable();
    }

    public void Dispose()
    {
        foreach (ResidentModel resident in _residentModels.Values)
        {
            gpu.Destroy(resident.Acceleration);
            gpu.Destroy(resident.Impostor);
            gpu.Destroy(resident.ImpostorUv);
        }
    }

    /// <summary>
    /// The pools models are copied into, asked for every frame before any model is, so what they hold stays.
    /// </summary>
    public Result Prepare(RenderGraph graph)
    {
        Result<GpuBuffer> vertices = graph.Buffer(
            GpuDescriptions.GetBuffer("Vertex pool", (long)VertexCapacity * GpuVertex.Bytes)
        );
        if (!vertices.Ok)
            return Result.Failure(vertices.Error);

        VertexPool = vertices.Value;
        Result<GpuBuffer> indices = graph.Buffer(
            GpuDescriptions.GetBuffer("Index pool", (long)IndexCapacity * sizeof(uint))
        );
        if (!indices.Ok)
            return Result.Failure(indices.Error);

        IndexPool = indices.Value;
        Result<GpuBuffer> clusters = graph.Buffer(
            GpuDescriptions.GetBuffer("Cluster pool", (long)ClusterCapacity * GpuCluster.Bytes)
        );
        if (!clusters.Ok)
            return Result.Failure(clusters.Error);

        ClusterPool = clusters.Value;
        Result<GpuBuffer> impostors = graph.Buffer(
            GpuDescriptions.GetBuffer("Impostor table", (long)ImpostorCapacity * GpuImpostor.Bytes)
        );
        if (!impostors.Ok)
            return Result.Failure(impostors.Error);

        ImpostorTable = impostors.Value;
        if (!graph.Features.HasRayQuery)
            return Result.Success();

        Result<GpuBuffer> leaves = graph.Buffer(
            GpuDescriptions.GetBuffer("Leaf clusters", (long)ClusterCapacity * sizeof(uint))
        );
        if (!leaves.Ok)
            return Result.Failure(leaves.Error);

        LeafClusters = leaves.Value;

        return Result.Success();
    }

    /// <summary>
    /// Evicts every model not asked for in <see cref="UnusedSec"/> that none of <paramref name="drawn"/> holds: its
    /// ranges go back to the pools' free lists and its acceleration structure is destroyed. Renderers not written since
    /// their rows were built ask for nothing, so once a model looks unused each model <paramref name="drawn"/> holds is
    /// asked for first.
    /// </summary>
    public void EvictUnusedModels(ReadOnlySpan<Renderer> drawn)
    {
        long now = Environment.TickCount64;
        if (!HasUnusedModel(now))
            return;

        foreach (Renderer renderer in drawn)
        {
            if (renderer.Model != Handle<Model>.None)
                Get(renderer.Model);
        }

        foreach ((ulong id, ResidentModel where) in _residentModels)
        {
            if (!IsUnused(id, now))
                continue;

            _vertexRanges.Free((int)where.VertexOffset, (int)where.VertexCount);
            _indexRanges.Free((int)where.IndexOffset, (int)where.IndexCount);
            _clusterRanges.Free((int)where.ClusterOffset, (int)where.ClusterCount);
            if (!where.Acceleration.IsNone)
                _leafRanges.Free((int)where.LeafOffset, (int)where.LeafCount);

            gpu.Destroy(where.Acceleration);
            if (!where.Impostor.IsNone)
            {
                gpu.Destroy(where.Impostor);
                gpu.Destroy(where.ImpostorUv);
                _impostorRows.Free((int)where.ImpostorRow, 1);
            }

            _residentModels.Remove(id);
            _askedModels.Remove(id);
            Log.Info(
                $"{assets.GetPath(new Handle<Model>(id)) ?? $"model {id}"} left the GPU: nothing asked for it in {UnusedSec} s."
            );
        }
    }

    /// <summary>
    /// Whether the texture is on the GPU, and its heap ids when it is; the first ask starts reading it.
    /// </summary>
    public ResourceState Get(Handle<Texture> handle, out uint texture, out uint sampler)
    {
        if (!_textures.TryGetValue(handle.Id, out TextureEntry? entry))
        {
            entry = new TextureEntry();
            _textures[handle.Id] = entry;
            LoadAsync(handle, entry).FireAndForget();
        }

        texture = entry.Texture.Id;
        sampler = entry.Sampler.Id;

        return entry.State;
    }

    /// <summary>
    /// Whether the material and the texture it names are on the GPU, or it is drawn as the failure; the first ask starts
    /// loading it.
    /// </summary>
    public ResourceState Get(Handle<Material> handle) => Get(new MaterialInstance { Handle = handle }, out _);

    /// <summary>
    /// Whether the slot's material and the texture it names are on the GPU, and the row the slot's params over the
    /// material's make: "color", "roughness", "metalness", "emissive" and the material's "colorMap" texture, each the
    /// default when left out. The default row while the material loads and without the texture while that loads; of the
    /// failure kind when the material did not load or names the failure shader, and the bare failure row when its
    /// texture failed. The first ask starts loading the material.
    /// </summary>
    public ResourceState Get(in MaterialInstance slot, out GpuMaterial row)
    {
        row = DefaultMaterial;
        if (!_materials.TryGetValue(slot.Handle.Id, out Material? asked))
        {
            _materials[slot.Handle.Id] = null;
            LoadAsync(slot.Handle).FireAndForget();

            return ResourceState.Loading;
        }

        if (asked is not { } material)
            return ResourceState.Loading;

        MaterialOverrides overrides = slot.Overrides;
        bool isFailure = material.Shader == assets.Find<Shader>(FailureShader);
        row = isFailure ? FailureMaterial : DefaultMaterial;
        if (TryGetParam("color", out MaterialParam? color))
            row.BaseColor = new Vector4(color.R, color.G, color.B, color.A);

        if (TryGetParam("roughness", out MaterialParam? roughness))
            row.Roughness = roughness.X;

        if (TryGetParam("metalness", out MaterialParam? metalness))
            row.Metalness = metalness.X;

        if (TryGetParam("emissive", out MaterialParam? emissive))
            row.Emissive = new Vector3(emissive.R, emissive.G, emissive.B);

        ResourceState colorMap = GetColorMap(material.Params, out uint texture, out uint sampler);
        switch (colorMap)
        {
            case ResourceState.Ready:
                row.BaseColorTexture = texture;
                row.BaseColorSampler = sampler;

                return isFailure ? ResourceState.Failed : ResourceState.Ready;
            case ResourceState.Failed:
                row = FailureMaterial;

                return ResourceState.Failed;
            case ResourceState.Loading:
                return ResourceState.Loading;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot), colorMap, "not a resource state");
        }

        bool TryGetParam(string name, [MaybeNullWhen(false)] out MaterialParam param) =>
            overrides.TryGet(name, out param) || material.Params.TryGetValue(name, out param);
    }

    /// <summary>
    /// Where the model is in the pools, or null while it is being made; the first ask starts making it, and every ask
    /// keeps it from being evicted. A model that could not be made is the failure model, marked so.
    /// </summary>
    public ResidentModel? Get(Handle<Model> model)
    {
        if (_failedModels.Contains(model.Id))
        {
            Handle<Model> failure = assets.GetFailure<Model, Model>();
            if (failure == Handle<Model>.None || _failedModels.Contains(failure.Id))
                return null;

            return Get(failure) is { } stand ? stand with { IsFailure = true } : null;
        }

        ref long asked = ref CollectionsMarshal.GetValueRefOrAddDefault(_askedModels, model.Id, out bool wasAsked);
        asked = Environment.TickCount64;
        if (!wasAsked)
            LoadAsync(model).FireAndForget();

        return _residentModels.TryGetValue(model.Id, out ResidentModel resident) ? resident : null;
    }

    /// <summary>
    /// Whether the model is in the pools, copied up in the last upload pass at the latest, or could not be made and the
    /// failure model it is drawn as has arrived in its place, or has no place to arrive in.
    /// </summary>
    public bool HasArrived(Handle<Model> model)
    {
        if (_residentModels.ContainsKey(model.Id))
            return true;

        if (!HasFailed(model))
            return false;

        Handle<Model> failure = assets.GetFailure<Model, Model>();

        return failure == Handle<Model>.None || failure == model || HasArrived(failure);
    }

    /// <summary>
    /// Whether the model could not be made, and is drawn as the failure model.
    /// </summary>
    public bool HasFailed(Handle<Model> model) => _failedModels.Contains(model.Id);

    /// <summary>
    /// Copies the models that arrived since the last frame, asks for every texture read and its sampler, and copies the
    /// levels of the textures that arrived, in the frame's upload pass.
    /// </summary>
    public void Upload(RenderGraph graph)
    {
        foreach ((MeshDerived derived, ResidentModel where) in _arrivedModels)
        {
            graph.Upload(VertexPool, derived.Vertices.AsSpan(), (ulong)where.VertexOffset * GpuVertex.Bytes);

            // Indices point into the whole pool, and clusters into the whole index pool.
            Span<uint> rebased = graph.Upload<uint>(
                IndexPool,
                derived.Indices.Length,
                (ulong)where.IndexOffset * sizeof(uint)
            );
            for (int i = 0; i < derived.Indices.Length; i++)
            {
                rebased[i] = derived.Indices[i] + where.VertexOffset;
            }

            Span<GpuCluster> rows = graph.Upload<GpuCluster>(
                ClusterPool,
                derived.Clusters.Length,
                (ulong)where.ClusterOffset * GpuCluster.Bytes
            );
            for (int i = 0; i < derived.Clusters.Length; i++)
            {
                GpuCluster cluster = derived.Clusters[i];
                cluster.IndexOffset += where.IndexOffset;
                rows[i] = cluster;
            }

            UploadLeaves(graph, derived, where);
            if (!where.Impostor.IsNone)
                UploadImpostor(graph, derived, where);
        }

        _arrivedModels.Clear();
        foreach (TextureEntry entry in _textures.Values)
        {
            Ask(graph, entry);
        }

        foreach ((Texture asset, TextureEntry entry) in _arrivedTextures)
        {
            if (entry.State != ResourceState.Ready)
                continue;

            for (int level = 0; level < asset.Levels.Length; level++)
            {
                TextureLevel source = asset.Levels[level];
                graph.Upload(entry.Texture, asset.Pixels.Span.Slice(source.Offset, source.Size), level);
            }
        }

        _arrivedTextures.Clear();
    }

    /// <summary>
    /// The texture and its sampler asked for, once it was read, and ready with their heap ids; failed, and drawn as the
    /// failure, when either was not made.
    /// </summary>
    private static void Ask(RenderGraph graph, TextureEntry entry)
    {
        if (entry.State == ResourceState.Failed || entry.Description is not { } description)
            return;

        Result<GpuTexture> texture = graph.Texture(description);
        Result<GpuSampler> sampler = graph.Sampler(entry.SamplerDescription);
        Result made = !texture.Ok ? texture : sampler;
        if (!made.Ok)
        {
            Log.Error($"{description.Name} is drawn as a failure: {made.Error}");
            entry.State = ResourceState.Failed;

            return;
        }

        entry.Texture = texture.Value;
        entry.Sampler = sampler.Value;
        entry.State = ResourceState.Ready;
    }

    private static FreeList CreateImpostorRows()
    {
        FreeList rows = new(ImpostorCapacity);
        rows.Allocate(1);

        return rows;
    }

    private static GpuFormat ToGpu(TextureFormat format) =>
        format switch
        {
            TextureFormat.Rgba8Unorm => GpuFormat.R8G8B8A8Unorm,
            TextureFormat.Rgba8Srgb => GpuFormat.R8G8B8A8Srgb,
            TextureFormat.Bc7Unorm => GpuFormat.Bc7Unorm,
            TextureFormat.Bc7Srgb => GpuFormat.Bc7Srgb,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "not a texture format"),
        };

    /// <summary>
    /// The state of the "colorMap" texture <paramref name="parameters"/> name, asking for it, with its heap ids once it
    /// is ready; ready, as nothing, when they name none.
    /// </summary>
    private ResourceState GetColorMap(MaterialParamDictionary parameters, out uint texture, out uint sampler)
    {
        if (parameters.TryGetValue("colorMap", out MaterialParam? colorMap) && colorMap.Texture != Handle<Texture>.None)
            return Get(colorMap.Texture, out texture, out sampler);

        texture = 0;
        sampler = 0;

        return ResourceState.Ready;
    }

    /// <summary>
    /// Whether some model on the GPU was not asked for in <see cref="UnusedSec"/> up to <paramref name="now"/>.
    /// </summary>
    private bool HasUnusedModel(long now)
    {
        foreach (ulong id in _residentModels.Keys)
        {
            if (IsUnused(id, now))
                return true;
        }

        return false;
    }

    private bool IsUnused(ulong model, long now) => now - _askedModels[model] >= UnusedSec * 1000L;

    private async Task LoadAsync(Handle<Material> handle)
    {
        Result<Material> loaded = await assets.LoadAsync(handle, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        if (!loaded.Ok)
        {
            Log.Error($"{assets.GetPath(handle)} is drawn as a failure: {loaded.Error}");
            _materials[handle.Id] = new Material { Shader = assets.Find<Shader>(FailureShader) };

            return;
        }

        _materials[handle.Id] = loaded.Value;
        // Asks for its texture now, so the loading gate waits for it before any row needs it.
        GetColorMap(loaded.Value.Params, out _, out _);
    }

    private async Task LoadAsync(Handle<Texture> handle, TextureEntry entry)
    {
        Result<Texture> loaded = await assets.LoadAsync(handle, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        string name = assets.GetPath(handle) ?? $"texture {handle.Id}";
        if (!loaded.Ok)
        {
            Log.Error($"{name} is drawn as a failure: {loaded.Error}");
            entry.State = ResourceState.Failed;

            return;
        }

        // The next upload asks for the texture and its sampler and copies its levels in.
        Texture asset = loaded.Value;
        entry.Description = new TextureDescription
        {
            Width = asset.Width,
            Height = asset.Height,
            Format = ToGpu(asset.Format),
            Usage = TextureUsages.Sampled | TextureUsages.CopyDestination,
            Levels = asset.Levels.Length,
            Name = name,
        };
        entry.SamplerDescription = new SamplerDescription
        {
            Filter = asset.Filter,
            Wrap = asset.Wrap,
            Anisotropy = asset.Filter == TextureFilter.Linear ? 8 : 1,
        };
        _arrivedTextures.Add((asset, entry));
    }

    private async Task LoadAsync(Handle<Model> model)
    {
        // The load preprocesses the model into the cache under its version. One loaded before the gem registered its
        // preprocessor, or one the cache could not keep, is made here, on a worker, and cached.
        Result<Model> loaded = await assets.LoadAsync(model, null, CancellationToken.None).ConfigureAwait(false);
        Result<MeshDerived> derived = loaded.Ok
            ? await caches
                .ReadAsync(model.Id, assets.GetVersion(model), models, CancellationToken.None)
                .ConfigureAwait(false)
            : Result.Failure(loaded.Error);
        if (loaded.Ok && !derived.Ok)
        {
            MeshDerived built = await Task.Run(() => models.Preprocess(loaded.Value)).ConfigureAwait(false);
            await caches
                .WriteAsync(model.Id, assets.GetVersion(model), built, models, CancellationToken.None)
                .ConfigureAwait(false);
            derived = Result.Success(built);
        }

        await threads.Enter(EngineThread.Render);
        string name = assets.GetPath(model) ?? $"model {model.Id}";
        if (!derived.Ok)
        {
            Log.Error($"{name} is drawn as the failure model: {derived.Error}");
            _failedModels.Add(model.Id);
            SettledModels++;

            return;
        }

        MeshDerived made = derived.Value;
        bool fits =
            _vertexRanges.CanAllocate(made.Vertices.Length)
            && _indexRanges.CanAllocate(made.Indices.Length)
            && _clusterRanges.CanAllocate(made.Clusters.Length);
        if (!fits)
        {
            Log.Error($"{name} is drawn as the failure model: the geometry pools are full.");
            _failedModels.Add(model.Id);
            SettledModels++;

            return;
        }

        ResidentModel where = new(
            (uint)_vertexRanges.Allocate(made.Vertices.Length),
            (uint)made.Vertices.Length,
            (uint)_indexRanges.Allocate(made.Indices.Length),
            (uint)made.Indices.Length,
            (uint)_clusterRanges.Allocate(made.Clusters.Length),
            (uint)made.Clusters.Length,
            made.BoundsCenter,
            made.BoundsRadius
        );
        // A leaf table is there only on a GPU that traces rays.
        if (!LeafClusters.IsNone)
            where = CreateAcceleration(name, made, where);

        // PROTO: a model that baked an impostor gets its atlases and a row of the impostor table.
        if (!made.Impostor.IsEmpty && _impostorRows.CanAllocate(1))
        {
            Result<GpuTexture> atlas = CreateAtlas($"{name} impostor", GpuFormat.R16G16B16A16Sfloat);
            Result<GpuTexture> uvAtlas = CreateAtlas($"{name} impostor uv", GpuFormat.R16G16Sfloat);
            if (atlas.Ok && uvAtlas.Ok)
            {
                where = where with
                {
                    Impostor = atlas.Value,
                    ImpostorUv = uvAtlas.Value,
                    ImpostorRow = (uint)_impostorRows.Allocate(1),
                };
            }
            else
            {
                gpu.Destroy(atlas.Value);
                gpu.Destroy(uvAtlas.Value);
                Log.Warn($"{name} draws no impostor: {(atlas.Ok ? uvAtlas.Error : atlas.Error)}");
            }
        }

        // An arrival is a use, so a model that took longer to make than it may go unused is copied up before it can be
        // evicted.
        _askedModels[model.Id] = Environment.TickCount64;
        _residentModels[model.Id] = where;
        _arrivedModels.Add((made, where));
        SettledModels++;
        int leafTriangles = 0;
        foreach (GpuCluster cluster in made.Clusters)
        {
            if (cluster.Error == 0)
                leafTriangles += (int)cluster.TriangleCount;
        }

        Log.Info(
            $"{name}: {made.Vertices.Length} vertices, {made.Indices.Length / 3} triangles in {made.Clusters.Length} clusters, {leafTriangles} in the leaves."
        );
    }

    /// <summary>
    /// The model's place with its acceleration structure over its leaf clusters, a geometry each, and their range in the
    /// leaf table; as it was, with none, when the structure could not be made.
    /// </summary>
    private ResidentModel CreateAcceleration(string name, MeshDerived made, ResidentModel where)
    {
        List<TriangleGeometry> geometries = [];
        foreach (GpuCluster cluster in made.Clusters)
        {
            if (cluster.Error != 0)
                continue;

            geometries.Add(
                new TriangleGeometry(
                    VertexPool,
                    0,
                    GpuVertex.Bytes,
                    VertexCapacity,
                    IndexPool,
                    (ulong)(where.IndexOffset + cluster.IndexOffset) * sizeof(uint),
                    cluster.TriangleCount
                )
            );
        }

        if (geometries.Count == 0 || !_leafRanges.CanAllocate(geometries.Count))
            return where;

        Result<GpuAcceleration> acceleration = gpu.CreateAcceleration(
            new BottomLevelDescription { Geometries = [.. geometries], Name = $"{name} acceleration" }
        );
        if (!acceleration.Ok)
        {
            Log.Warn($"{name} has no acceleration structure, so rays pass through it: {acceleration.Error}");

            return where;
        }

        return where with
        {
            LeafOffset = (uint)_leafRanges.Allocate(geometries.Count),
            LeafCount = (uint)geometries.Count,
            Acceleration = acceleration.Value,
        };
    }

    /// <summary>
    /// The model's leaf clusters into the leaf table and its acceleration structure built, after its pools landed.
    /// </summary>
    private void UploadLeaves(RenderGraph graph, MeshDerived derived, ResidentModel where)
    {
        if (where.Acceleration.IsNone)
            return;

        List<uint> leaves = [];
        for (int i = 0; i < derived.Clusters.Length; i++)
        {
            GpuCluster cluster = derived.Clusters[i];
            if (cluster.Error == 0)
                leaves.Add(where.ClusterOffset + (uint)i);
        }

        graph.Upload(LeafClusters, CollectionsMarshal.AsSpan(leaves), (ulong)where.LeafOffset * sizeof(uint));
        graph.BuildAcceleration(where.Acceleration);
    }

    /// <summary>
    /// PROTO: an impostor atlas of <see cref="MeshDerived.ImpostorSize"/> texels a side, in <paramref name="format"/>.
    /// </summary>
    private Result<GpuTexture> CreateAtlas(string name, GpuFormat format) =>
        gpu.CreateTexture(
            new TextureDescription
            {
                Width = MeshDerived.ImpostorSize,
                Height = MeshDerived.ImpostorSize,
                Format = format,
                Usage = TextureUsages.Sampled | TextureUsages.CopyDestination,
                Levels = 1,
                Name = name,
            }
        );

    /// <summary>
    /// PROTO: the model's impostor atlases copied up, and its row of the impostor table written.
    /// </summary>
    private void UploadImpostor(RenderGraph graph, MeshDerived derived, ResidentModel where)
    {
        graph.Upload(where.Impostor, MemoryMarshal.AsBytes(derived.Impostor.AsSpan()));
        graph.Upload(where.ImpostorUv, MemoryMarshal.AsBytes(derived.ImpostorUv.AsSpan()));
        GpuImpostor row = new()
        {
            BoundsMin = derived.BoundsMin,
            Atlas = where.Impostor.Id,
            BoundsMax = derived.BoundsMax,
            UvAtlas = where.ImpostorUv.Id,
        };
        graph.Upload(ImpostorTable, [row], (ulong)where.ImpostorRow * GpuImpostor.Bytes);
    }

    /// <summary>
    /// A texture asked for: where it is in the loading, what it and its sampler are made to once it is read, and their
    /// heap ids once they are made.
    /// </summary>
    private sealed class TextureEntry
    {
        public ResourceState State { get; set; }

        public TextureDescription? Description { get; set; }

        public SamplerDescription SamplerDescription { get; set; }

        public GpuTexture Texture { get; set; }

        public GpuSampler Sampler { get; set; }
    }
}

/// <summary>
/// Where a model is in the pools, each range its offset and count, and the sphere around it in its own frame;
/// <paramref name="IsFailure"/> when it stands in for a model that could not be made, to be drawn as a failure.
/// </summary>
internal readonly record struct ResidentModel(
    uint VertexOffset,
    uint VertexCount,
    uint IndexOffset,
    uint IndexCount,
    uint ClusterOffset,
    uint ClusterCount,
    Vector3 BoundsCenter,
    float BoundsRadius,
    bool IsFailure = false,
    uint LeafOffset = 0,
    uint LeafCount = 0,
    GpuAcceleration Acceleration = default,
    GpuTexture Impostor = default,
    GpuTexture ImpostorUv = default,
    uint ImpostorRow = 0
);
