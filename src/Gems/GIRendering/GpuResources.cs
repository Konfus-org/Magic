using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
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
/// material asset by its handle, read once, with the textures it names asked for and its shader's struct reflected,
/// and handed out as the row a slot's params make over its own, packed as that struct lays them out; a model asset by
/// its handle, made by the preprocessor on a worker, settled into the pools of vertices, indices, clusters and leaf clusters with its acceleration structure at the start of the next
/// frame, and copied up in it. The pools, the textures and each resident model's acceleration structure and atlases are
/// asked of the graph every frame, so the GPU provider keeps what they hold. Each pool hands out ranges first fit from a
/// free list. A model not asked for in <see cref="UnusedSec"/>, and held by no renderer drawn, is evicted: its ranges go
/// back to the free lists and its structure and atlases are no longer asked for, so the GPU provider frees them, and the
/// next ask makes it again.
/// </summary>
internal sealed class GpuResources(
    ModelPreprocessor models,
    Caches caches,
    AssetManager assets,
    IShaderCompiler shaders,
    Threads threads
)
{
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

    /// <summary>
    /// Each material shader's struct as reflection lays it out, by the shader's id, null while it is reflected; its
    /// fields are default when it was not, and its materials are drawn as the failure.
    /// </summary>
    private readonly Dictionary<ulong, MaterialLayout?> _layouts = [];
    private readonly List<ShaderTypeArgument> _materialShaders = [];

    // The impostor table's rows; row 0 is no impostor, so it is taken from the start.
    private readonly FreeList _impostorRows = CreateImpostorRows();

    /// <summary>
    /// Each model asked for and not evicted, by its id, made or not, with when it was last asked, in
    /// <see cref="Environment.TickCount64"/> milliseconds.
    /// </summary>
    private readonly Dictionary<ulong, long> _askedModels = [];
    private readonly Dictionary<ulong, ResidentModel> _residentModels = [];
    private readonly HashSet<ulong> _failedModels = [];
    private readonly List<(MeshDerived Derived, ResidentModel Where)> _arrivedModels = [];

    /// <summary>
    /// The models made since the last frame, by their id with their name, which the next <see cref="SettleModels"/>
    /// settles.
    /// </summary>
    private readonly List<(ulong Model, string Name, MeshDerived Made)> _madeModels = [];
    private readonly FreeList _vertexRanges = new(VertexCapacity);
    private readonly FreeList _indexRanges = new(IndexCapacity);
    private readonly FreeList _clusterRanges = new(ClusterCapacity);
    private readonly FreeList _leafRanges = new(ClusterCapacity);

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
    /// Every resident model's impostor, a <see cref="GpuImpostor"/> a row, by the row its instances name.
    /// </summary>
    public GpuBuffer ImpostorTable { get; private set; }

    /// <summary>
    /// The material shaders rows are drawn with, each as a resolve pass specialises to it, in the order a row numbers
    /// them.
    /// </summary>
    public IReadOnlyList<ShaderTypeArgument> MaterialShaders => _materialShaders;

    /// <summary>
    /// The textures shaders read through the heap, by the id in a row rather than by a pass's name: every material's
    /// texture on the GPU and every resident model's impostor atlases, which are asked of the graph here, every frame that
    /// reads them. A pass that samples them names them in its reads, so each is readable, its copy done, before the pass
    /// runs.
    /// </summary>
    public ImmutableArray<GpuResource> GetHeapTextures(RenderGraph graph)
    {
        ImmutableArray<GpuResource>.Builder textures = ImmutableArray.CreateBuilder<GpuResource>();
        foreach (TextureEntry entry in _textures.Values)
        {
            if (entry.State == ResourceState.Ready)
                textures.Add(entry.Texture);
        }

        foreach (ResidentModel resident in _residentModels.Values)
        {
            if (resident.Impostor is not { } atlas || resident.ImpostorUv is not { } uvAtlas)
                continue;

            if (graph.Texture(atlas) is { Ok: true } surface)
                textures.Add(surface.Value);

            if (graph.Texture(uvAtlas) is { Ok: true } uvs)
                textures.Add(uvs.Value);
        }

        return textures.ToImmutable();
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
    /// Every resident model's acceleration structure asked for, so the GPU provider keeps it, and the models made since
    /// the last frame settled into the pools, after the pools were asked for and before any renderer's row is built.
    /// </summary>
    public void SettleModels(RenderGraph graph)
    {
        foreach (ResidentModel resident in _residentModels.Values)
        {
            if (resident.AccelerationDescription is { } acceleration)
                _ = graph.Acceleration(acceleration);
        }

        foreach ((ulong model, string name, MeshDerived made) in _madeModels)
        {
            Settle(graph, model, name, made);
        }

        _madeModels.Clear();
    }

    /// <summary>
    /// Evicts every model not asked for in <see cref="UnusedSec"/> that none of <paramref name="drawn"/> holds: its
    /// ranges go back to the pools' free lists and its acceleration structure and atlases are no longer asked for.
    /// Renderers not written since their rows were built ask for nothing, so once a model looks unused each model
    /// <paramref name="drawn"/> holds is asked for first.
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

            // Its acceleration structure and atlases go once no frame asks for them.
            if (where.ImpostorRow != 0)
                _impostorRows.Free((int)where.ImpostorRow, 1);

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
    /// Whether the material, its shader's struct and the textures it names are on the GPU, or it is drawn as the failure;
    /// the first ask starts loading it.
    /// </summary>
    public ResourceState Get(Handle<Material> handle) => Get(new MaterialInstance { Handle = handle }, out _);

    /// <summary>
    /// Whether the slot's material, its shader's struct and the textures it names are on the GPU, and the row the slot's
    /// params over the material's make: each field of the struct by its name, a texture's heap id in a <c>TextureRef</c>
    /// and its sampler's in the <c>SamplerRef</c> named for it with "Sampler" after, and 0 where neither sets one. Failed
    /// when the material, its shader's struct or a texture did not load, or the material is the failure. The first ask
    /// starts loading the material.
    /// </summary>
    public ResourceState Get(in MaterialInstance slot, out GpuMaterial row)
    {
        row = default;
        if (!_materials.TryGetValue(slot.Handle.Id, out Material? asked))
        {
            _materials[slot.Handle.Id] = null;
            LoadAsync(slot.Handle).FireAndForget();

            return ResourceState.Loading;
        }

        if (asked is not { } material || _layouts.GetValueOrDefault(material.Shader.Id) is not { } layout)
            return ResourceState.Loading;

        if (layout.Fields.IsDefault)
            return ResourceState.Failed;

        MaterialOverrides overrides = slot.Overrides;
        ResourceState state =
            material.Shader == assets.Find<Shader>(FailureShader) ? ResourceState.Failed : ResourceState.Ready;
        row.Shader = layout.Shader;
        Span<uint> words = row.Params;
        foreach (ShaderField field in layout.Fields)
        {
            if (!TryGetParam(field.Name, out MaterialParam? param))
                continue;

            Span<uint> into = words.Slice((int)field.Offset / 4, (int)field.Size / 4);
            if (field.Struct == "TextureRef")
            {
                ResourceState texture = PackTexture(layout, field.Name, param.Texture, words);
                state = texture == ResourceState.Ready || state == ResourceState.Failed ? state : texture;

                continue;
            }

            // PROTO: a param that sets a colour fills the field from it, and one that sets none from its vector.
            bool isColor = param.R != 0 || param.G != 0 || param.B != 0 || param.A != 0;
            ReadOnlySpan<float> values = isColor
                ? [param.R, param.G, param.B, param.A]
                : [param.X, param.Y, param.Z, param.W];
            for (int i = 0; i < into.Length && i < values.Length; i++)
            {
                into[i] = BitConverter.SingleToUInt32Bits(values[i]);
            }
        }

        return state;

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
            if (where.ImpostorRow != 0)
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
    /// Writes <paramref name="texture"/>'s heap id into the field named <paramref name="name"/> of <paramref name="words"/>,
    /// and its sampler's into the field named for it with "Sampler" after, asking for it; its state, ready as nothing
    /// when it is none.
    /// </summary>
    private ResourceState PackTexture(MaterialLayout layout, string name, Handle<Texture> texture, Span<uint> words)
    {
        if (texture == Handle<Texture>.None)
            return ResourceState.Ready;

        ResourceState state = Get(texture, out uint id, out uint sampler);
        foreach (ShaderField field in layout.Fields)
        {
            if (field.Name == name)
                words[(int)field.Offset / 4] = id;
            else if (field.Struct == "SamplerRef" && field.Name == name + "Sampler")
                words[(int)field.Offset / 4] = sampler;
        }

        return state;
    }

    /// <summary>
    /// Reflects the struct of <paramref name="shader"/>, which a material draws with, once: named for its file, as
    /// Material/Standard.slang declares Standard. It numbers the shader for the rows once it has, or leaves it failed
    /// when it can't, or the struct is more than a row holds.
    /// </summary>
    private async Task ReflectAsync(Handle<Shader> shader)
    {
        string path = assets.GetPath(shader) ?? $"shader {shader.Id}";
        ShaderTypeArgument type = new(shader, Path.GetFileNameWithoutExtension(path));
        Result<ImmutableArray<ShaderField>> fields = await shaders
            .ReflectAsync(type, CancellationToken.None)
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        int bytes = fields.Ok
            ? fields.Value.Select(field => (int)(field.Offset + field.Size)).DefaultIfEmpty().Max()
            : 0;
        if (!fields.Ok || bytes > GpuMaterial.Bytes - 4)
        {
            string why = fields.Ok
                ? $"its params are {bytes} bytes, over a row's {GpuMaterial.Bytes - 4}"
                : fields.Error;
            Log.Error($"Materials drawn with {path} are drawn as the failure: {why}");
            _layouts[shader.Id] = new MaterialLayout(default, 0);

            return;
        }

        _layouts[shader.Id] = new MaterialLayout(fields.Value, (uint)_materialShaders.Count);
        _materialShaders.Add(type);
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
            Log.Error($"{assets.GetPath(handle)} is drawn as a failure: {loaded.Error}");

        Material material = loaded.Ok ? loaded.Value : new Material { Shader = assets.Find<Shader>(FailureShader) };
        _materials[handle.Id] = material;

        // Its shader's struct and its textures are asked for now, so the loading gate waits for them before any row
        // needs them.
        if (_layouts.TryAdd(material.Shader.Id, null))
            ReflectAsync(material.Shader).FireAndForget();

        foreach (MaterialParam param in material.Params.Values)
        {
            if (param.Texture != Handle<Texture>.None)
                Get(param.Texture, out _, out _);
        }
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

        _madeModels.Add((model.Id, name, derived.Value));
    }

    /// <summary>
    /// The model made into the pools' ranges, with its acceleration structure over its leaf clusters on a GPU that
    /// traces rays and its impostor's table row and atlases when it baked one, all copied up by the next upload; the
    /// failure model when the pools are full.
    /// </summary>
    private void Settle(RenderGraph graph, ulong model, string name, MeshDerived made)
    {
        SettledModels++;
        bool fits =
            _vertexRanges.CanAllocate(made.Vertices.Length)
            && _indexRanges.CanAllocate(made.Indices.Length)
            && _clusterRanges.CanAllocate(made.Clusters.Length);
        if (!fits)
        {
            Log.Error($"{name} is drawn as the failure model: the geometry pools are full.");
            _failedModels.Add(model);

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
            where = AskAcceleration(graph, name, made, where);

        // A model that baked an impostor gets a row of the impostor table, and its atlases' descriptions, which the graph
        // makes on the first ask and keeps while frames read them.
        if (!made.Impostor.IsEmpty && _impostorRows.CanAllocate(1))
        {
            where = where with
            {
                Impostor = GetAtlasDescription($"{name} impostor", GpuFormat.R16G16B16A16Sfloat),
                ImpostorUv = GetAtlasDescription($"{name} impostor uv", GpuFormat.R16G16Sfloat),
                ImpostorRow = (uint)_impostorRows.Allocate(1),
            };
        }

        // An arrival is a use, so a model that took longer to make than it may go unused is copied up before it can be
        // evicted.
        _askedModels[model] = Environment.TickCount64;
        _residentModels[model] = where;
        _arrivedModels.Add((made, where));
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
    /// The model's place with its acceleration structure over its leaf clusters, a geometry each, asked for, what it is
    /// made to, and their range in the leaf table; as it was, with none, when the structure could not be made.
    /// </summary>
    private ResidentModel AskAcceleration(RenderGraph graph, string name, MeshDerived made, ResidentModel where)
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

        BottomLevelDescription description = new() { Geometries = [.. geometries], Name = $"{name} acceleration" };
        Result<GpuAcceleration> acceleration = graph.Acceleration(description);
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
            AccelerationDescription = description,
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
    /// An impostor atlas of <see cref="MeshDerived.ImpostorSize"/> texels a side, in <paramref name="format"/>.
    /// </summary>
    private static TextureDescription GetAtlasDescription(string name, GpuFormat format) =>
        new()
        {
            Width = MeshDerived.ImpostorSize,
            Height = MeshDerived.ImpostorSize,
            Format = format,
            Usage = TextureUsages.Sampled | TextureUsages.CopyDestination,
            Levels = 1,
            Name = name,
        };

    /// <summary>
    /// The model's impostor atlases asked of the graph and copied up, and its row of the impostor table written; the row
    /// stays empty when an atlas was not made, and the model draws no impostor.
    /// </summary>
    private void UploadImpostor(RenderGraph graph, MeshDerived derived, ResidentModel where)
    {
        if (where.Impostor is not { } atlasDescription || where.ImpostorUv is not { } uvDescription)
            return;

        Result<GpuTexture> atlas = graph.Texture(atlasDescription);
        Result<GpuTexture> uvAtlas = graph.Texture(uvDescription);
        if (!atlas.Ok || !uvAtlas.Ok)
        {
            Log.Warn($"{atlasDescription.Name} is not drawn: {(atlas.Ok ? uvAtlas.Error : atlas.Error)}");

            return;
        }

        graph.Upload(atlas.Value, MemoryMarshal.AsBytes(derived.Impostor.AsSpan()));
        graph.Upload(uvAtlas.Value, MemoryMarshal.AsBytes(derived.ImpostorUv.AsSpan()));
        GpuImpostor row = new()
        {
            BoundsMin = derived.BoundsMin,
            Atlas = atlas.Value.Id,
            BoundsMax = derived.BoundsMax,
            UvAtlas = uvAtlas.Value.Id,
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

    /// <summary>
    /// A material shader's struct as its rows are packed: its fields as reflection lays them out, default when it could
    /// not be, and the number its rows name it by.
    /// </summary>
    private readonly record struct MaterialLayout(ImmutableArray<ShaderField> Fields, uint Shader);
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
    BottomLevelDescription? AccelerationDescription = null,
    TextureDescription? Impostor = null,
    TextureDescription? ImpostorUv = null,
    uint ImpostorRow = 0
);
