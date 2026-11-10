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
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GpuWorld;

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
/// its handle, made by the preprocessor on a worker, settled into the pools of vertices and indices, the mesh table
/// with its acceleration structure and the model table at the start of the next frame, and copied up in it. The pools,
/// the textures and each resident model's acceleration structure are asked of the graph every frame, so the GPU
/// provider keeps what they hold. Each pool hands out ranges first fit from a free list. A model not asked for in
/// <see cref="UnusedSec"/>, and held by no renderer drawn, is evicted: its ranges go back to the free lists, its row of
/// the model table is emptied and its structure is no longer asked for, so the GPU provider frees it, and the next ask
/// makes it again.
/// </summary>
internal sealed class GpuResources(
    ModelPreprocessor models,
    Caches caches,
    AssetManager assets,
    IShaderCompiler shaders,
    Threads threads
)
{
    private const int ModelCapacity = 4096;

    private const string FailureShader = "Shaders/Material/Failure.slang";

    /// <summary>
    /// How long, in seconds of real time, a model stays on the GPU after its last ask.
    /// </summary>
    private const int UnusedSec = 60;

    private static readonly int _vertexCapacity = (int)(SharedResources.Vertices.Bytes / GpuVertex.Bytes);

    private static readonly int _indexCapacity = (int)(SharedResources.Indices.Bytes / sizeof(uint));

    private static readonly int _meshCapacity = (int)(SharedResources.Meshes.Bytes / GpuMesh.Bytes);

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

    /// <summary>
    /// What each effect shader's struct declares it needs, by the shader's id, null while it is reflected.
    /// </summary>
    private readonly Dictionary<ulong, EffectNeeds?> _effectNeeds = [];

    // The models' indices; row 0 is none, so it is taken from the start.
    private readonly FreeList _modelRows = CreateRows(ModelCapacity);

    /// <summary>
    /// Each resident model by its model index, none where no model is.
    /// </summary>
    private readonly ModelPlace[] _models = new ModelPlace[ModelCapacity];

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

    private readonly FreeList _vertexRanges = new(_vertexCapacity);

    private readonly FreeList _indexRanges = new(_indexCapacity);

    private readonly FreeList _meshRanges = new(_meshCapacity);

    /// <summary>
    /// How many times models were made, failed or had their acceleration structure moved by the GPU provider, ever:
    /// when it changes, a renderer waiting for its model may draw now, and every row is built again.
    /// </summary>
    public int SettledModels { get; private set; }

    public GpuBuffer VertexPool { get; private set; }

    public GpuBuffer IndexPool { get; private set; }

    /// <summary>
    /// Every resident model's meshes as rays see them, a <see cref="GpuMesh"/> a row, in the order its acceleration
    /// structure's geometries name them; none on a GPU that traces no rays.
    /// </summary>
    public GpuBuffer Meshes { get; private set; }

    /// <summary>
    /// Each resident model by its model index, the one its instances name, none where no model is.
    /// </summary>
    public ReadOnlyMemory<ModelPlace> Models => _models;

    /// <summary>
    /// The material shaders rows are drawn with, each as a resolve pass specialises to it, in the order a row numbers
    /// them.
    /// </summary>
    public IReadOnlyList<ShaderTypeArgument> MaterialShaders => _materialShaders;

    /// <summary>
    /// The textures shaders read through the heap, by the id in a row rather than by a pass's name: every material's
    /// texture on the GPU. A pass that samples them names them in its reads, so each is readable, its copy done, before
    /// the pass runs.
    /// </summary>
    public ImmutableArray<GpuResource> GetHeapTextures()
    {
        ImmutableArray<GpuResource>.Builder textures = ImmutableArray.CreateBuilder<GpuResource>();
        foreach (TextureEntry entry in _textures.Values)
        {
            if (entry.State == ResourceState.Ready)
                textures.Add(entry.Texture);
        }

        return textures.ToImmutable();
    }

    /// <summary>
    /// The pools models are copied into, asked for every frame before any model is, so what they hold stays.
    /// </summary>
    public Result Prepare(RenderGraph graph)
    {
        Result<GpuBuffer> vertices = graph.Buffer(SharedResources.Vertices);
        if (!vertices.Ok)
            return Result.Failure(vertices.Error);

        VertexPool = vertices.Value;
        Result<GpuBuffer> indices = graph.Buffer(SharedResources.Indices);
        if (!indices.Ok)
            return Result.Failure(indices.Error);

        IndexPool = indices.Value;
        if (!graph.Features.HasRayQuery)
            return Result.Success();

        Result<GpuBuffer> meshes = graph.Buffer(SharedResources.Meshes);
        if (!meshes.Ok)
            return Result.Failure(meshes.Error);

        Meshes = meshes.Value;

        return Result.Success();
    }

    /// <summary>
    /// Every resident model's acceleration structure asked for, so the GPU provider keeps it, and the models made since
    /// the last frame settled into the pools, after the pools were asked for and before any renderer's row is built.
    /// A structure the provider compacted comes back moved, and every row is built again to refer to it there.
    /// </summary>
    public void SettleModels(RenderGraph graph)
    {
        foreach (ulong model in _residentModels.Keys)
        {
            ref ResidentModel resident = ref CollectionsMarshal.GetValueRefOrNullRef(_residentModels, model);
            if (resident.AccelerationDescription is not { } description)
                continue;

            Result<GpuAcceleration> acceleration = graph.Acceleration(description);
            if (!acceleration.Ok || acceleration.Value == resident.Acceleration)
                continue;

            resident = resident with { Acceleration = acceleration.Value };
            SettledModels++;
        }

        foreach ((ulong model, string name, MeshDerived made) in _madeModels)
        {
            Settle(graph, model, name, made);
        }

        _madeModels.Clear();
    }

    /// <summary>
    /// Evicts every model not asked for in <see cref="UnusedSec"/> that none of <paramref name="drawn"/> holds: its
    /// ranges go back to the pools' free lists, its row of the model table is emptied and its acceleration structure is
    /// no longer asked for.
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
            _modelRows.Free((int)where.ModelIndex, 1);
            _models[where.ModelIndex] = default;

            // Its acceleration structure goes once no frame asks for it.
            if (!where.Acceleration.IsNone)
                _meshRanges.Free((int)where.MeshOffset, (int)where.MeshCount);

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
    /// starts loading the material. The row names its shader by its place in <see cref="MaterialShaders"/>, which it
    /// takes on the first row that names it.
    /// </summary>
    public ResourceState Get(in MaterialInstance slot, out GpuMaterial row)
    {
        ResourceState state = Pack(slot, out row, out MaterialLayout layout);
        if (!layout.Fields.IsDefault)
            row.Shader = GetMaterialShader(layout.Shader) | (layout.IsTranslucent ? GpuMaterial.TranslucentBit : 0);

        return state;
    }

    /// <summary>
    /// Whether the effect's material, its shader's struct and the textures it names are on the GPU, its row packed as
    /// <see cref="Get(in MaterialInstance, out GpuMaterial)"/> packs a surface's, and the struct its pass is specialised
    /// to, with what it declares it needs. An effect's shader is not one of the <see cref="MaterialShaders"/>, which
    /// only surfaces are resolved with.
    /// </summary>
    public ResourceState GetEffect(
        in MaterialInstance slot,
        out GpuMaterial row,
        out ShaderTypeArgument shader,
        out EffectNeeds needs
    )
    {
        ResourceState state = Pack(slot, out row, out MaterialLayout layout);
        shader = layout.Shader;
        needs = default;
        if (state != ResourceState.Ready)
            return state;

        // What it needs is reflected on its first ask, only an effect's, and it waits for that.
        if (!_effectNeeds.TryGetValue(shader.Module.Id, out EffectNeeds? declared))
        {
            _effectNeeds[shader.Module.Id] = null;
            ReflectNeedsAsync(shader).FireAndForget();
        }

        needs = declared ?? default;

        return declared is null ? ResourceState.Loading : state;
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

            // Indices point into the whole pool.
            Span<uint> rebased = graph.Upload<uint>(
                IndexPool,
                derived.Indices.Length,
                (ulong)where.IndexOffset * sizeof(uint)
            );
            for (int i = 0; i < derived.Indices.Length; i++)
            {
                rebased[i] = derived.Indices[i] + where.VertexOffset;
            }

            UploadMeshes(graph, derived, where);
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

    private static FreeList CreateRows(int capacity)
    {
        FreeList rows = new(capacity);
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
    /// The model's meshes as rays see them, from <paramref name="indexOffset"/> in the index pool, with their
    /// triangles, leaving out a mesh with none.
    /// </summary>
    private static List<(GpuMesh Mesh, uint Triangles)> GetMeshes(MeshDerived made, uint indexOffset)
    {
        List<(GpuMesh Mesh, uint Triangles)> meshes = [];
        for (int i = 0; i < made.Meshes.Length; i++)
        {
            GpuMesh mesh = made.Meshes[i];
            uint end = i + 1 < made.Meshes.Length ? made.Meshes[i + 1].IndexOffset : (uint)made.Indices.Length;
            uint triangles = (end - mesh.IndexOffset) / 3;
            if (triangles > 0)
                meshes.Add((mesh with { IndexOffset = indexOffset + mesh.IndexOffset }, triangles));
        }

        return meshes;
    }

    /// <summary>
    /// Whether the slot's material, its shader's struct and the textures it names are on the GPU, its params packed into
    /// the row, and its shader's struct as reflection laid it out; the default struct while it loads.
    /// </summary>
    private ResourceState Pack(in MaterialInstance slot, out GpuMaterial row, out MaterialLayout layout)
    {
        row = default;
        layout = default;
        if (!_materials.TryGetValue(slot.Handle.Id, out Material? asked))
        {
            _materials[slot.Handle.Id] = null;
            LoadAsync(slot.Handle).FireAndForget();

            return ResourceState.Loading;
        }

        if (asked is not { } material || _layouts.GetValueOrDefault(material.Shader.Id) is not { } reflected)
            return ResourceState.Loading;

        layout = reflected;
        if (layout.Fields.IsDefault)
            return ResourceState.Failed;

        MaterialOverrides overrides = slot.Overrides;
        ResourceState state =
            material.Shader == assets.Find<Shader>(FailureShader) ? ResourceState.Failed : ResourceState.Ready;
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
    /// Material/Standard.slang declares Standard. It leaves it failed when it can't, or the struct is more than a row
    /// holds.
    /// </summary>
    private async Task ReflectAsync(Handle<Shader> shader)
    {
        string path = assets.GetPath(shader) ?? $"shader {shader.Id}";
        ShaderTypeArgument type = new(shader, Path.GetFileNameWithoutExtension(path));
        Result<ImmutableArray<ShaderField>> fields = await shaders
            .ReflectAsync(type, CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> translucent = await shaders
            .ReflectConstantAsync(type, "Translucent", CancellationToken.None)
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
            _layouts[shader.Id] = new MaterialLayout(default, type, false);

            return;
        }

        _layouts[shader.Id] = new MaterialLayout(fields.Value, type, translucent.Ok && translucent.Value != 0);
    }

    /// <summary>
    /// Reflects what the effect shader's struct declares it needs, once: none of what it declares nothing of.
    /// </summary>
    private async Task ReflectNeedsAsync(ShaderTypeArgument shader)
    {
        Result<long> levels = await shaders
            .ReflectConstantAsync(shader, "Levels", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> jitters = await shaders
            .ReflectConstantAsync(shader, "Jitters", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> rays = await shaders
            .ReflectConstantAsync(shader, "Rays", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> noUpscaling = await shaders
            .ReflectConstantAsync(shader, "NoUpscaling", CancellationToken.None)
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        bool hasJitter = jitters.Ok && jitters.Value != 0;
        _effectNeeds[shader.Module.Id] = new EffectNeeds(
            levels.Ok ? (int)levels.Value : 0,
            hasJitter,
            rays.Ok && rays.Value != 0,
            hasJitter && !(noUpscaling.Ok && noUpscaling.Value != 0)
        );
    }

    /// <summary>
    /// The place of <paramref name="shader"/> in <see cref="MaterialShaders"/>, which it takes at the end the first time.
    /// </summary>
    private uint GetMaterialShader(ShaderTypeArgument shader)
    {
        int number = _materialShaders.IndexOf(shader);
        if (number >= 0)
            return (uint)number;

        _materialShaders.Add(shader);

        return (uint)_materialShaders.Count - 1;
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
    /// The model made into the pools' ranges and given its index and its row of the model table, with its acceleration
    /// structure over its meshes on a GPU that traces rays, all copied up by the next upload; the failure model when the
    /// pools are full.
    /// </summary>
    private void Settle(RenderGraph graph, ulong model, string name, MeshDerived made)
    {
        SettledModels++;
        bool fits =
            _vertexRanges.CanAllocate(made.Vertices.Length)
            && _indexRanges.CanAllocate(made.Indices.Length)
            && _modelRows.CanAllocate(1);
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
            made.BoundsCenter,
            made.BoundsRadius,
            made.BoundsMin,
            made.BoundsMax,
            (uint)_modelRows.Allocate(1)
        );
        // A mesh table is there only on a GPU that traces rays.
        if (!Meshes.IsNone)
            where = AskAcceleration(graph, name, made, where);

        // An arrival is a use, so a model that took longer to make than it may go unused is copied up before it can be
        // evicted.
        _askedModels[model] = Environment.TickCount64;
        _residentModels[model] = where;
        _models[where.ModelIndex] = new ModelPlace(new Handle<Model>(model), where.VertexOffset);
        _arrivedModels.Add((made, where));
        Log.Info($"{name}: {made.Vertices.Length} vertices, {made.Indices.Length / 3} triangles.");
    }

    /// <summary>
    /// The model's place with its acceleration structure over its meshes, a geometry each, asked for, what it is made
    /// to, and their range in the mesh table; as it was, with none, when the structure could not be made.
    /// </summary>
    private ResidentModel AskAcceleration(RenderGraph graph, string name, MeshDerived made, ResidentModel where)
    {
        List<(GpuMesh Mesh, uint Triangles)> meshes = GetMeshes(made, where.IndexOffset);
        if (meshes.Count == 0 || !_meshRanges.CanAllocate(meshes.Count))
            return where;

        List<TriangleGeometry> geometries = [];
        foreach ((GpuMesh mesh, uint triangles) in meshes)
        {
            geometries.Add(
                new TriangleGeometry(
                    VertexPool,
                    0,
                    GpuVertex.Bytes,
                    (uint)_vertexCapacity,
                    IndexPool,
                    (ulong)mesh.IndexOffset * sizeof(uint),
                    triangles
                )
            );
        }

        // The indices hold the vertex offset, so the name does too: one name and description are one set of triangles.
        BottomLevelDescription description = new()
        {
            Geometries = [.. geometries],
            Name = $"{name} acceleration {where.VertexOffset}",
        };
        Result<GpuAcceleration> acceleration = graph.Acceleration(description);
        if (!acceleration.Ok)
        {
            Log.Warn($"{name} has no acceleration structure, so rays pass through it: {acceleration.Error}");

            return where;
        }

        return where with
        {
            MeshOffset = (uint)_meshRanges.Allocate(meshes.Count),
            MeshCount = (uint)meshes.Count,
            Acceleration = acceleration.Value,
            AccelerationDescription = description,
        };
    }

    /// <summary>
    /// The model's meshes into the mesh table and its acceleration structure built, after its pools landed.
    /// </summary>
    private void UploadMeshes(RenderGraph graph, MeshDerived derived, ResidentModel where)
    {
        if (where.Acceleration.IsNone)
            return;

        List<(GpuMesh Mesh, uint Triangles)> meshes = GetMeshes(derived, where.IndexOffset);
        Span<GpuMesh> rows = graph.Upload<GpuMesh>(Meshes, meshes.Count, (ulong)where.MeshOffset * GpuMesh.Bytes);
        for (int i = 0; i < meshes.Count; i++)
        {
            rows[i] = meshes[i].Mesh;
        }

        graph.BuildAcceleration(where.Acceleration);
    }

    /// <summary>
    /// A material shader's struct as its rows are packed: its fields as reflection lays them out, default when it could
    /// not be, and the struct a pass is specialised to.
    /// </summary>
    private readonly record struct MaterialLayout(
        ImmutableArray<ShaderField> Fields,
        ShaderTypeArgument Shader,
        bool IsTranslucent
    );

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
/// Where a model is in the pools, each range its offset and count, and the sphere and the box around it in its own frame;
/// <paramref name="IsFailure"/> when it stands in for a model that could not be made, to be drawn as a failure.
/// </summary>
internal readonly record struct ResidentModel(
    uint VertexOffset,
    uint VertexCount,
    uint IndexOffset,
    uint IndexCount,
    Vector3 BoundsCenter,
    float BoundsRadius,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    uint ModelIndex,
    bool IsFailure = false,
    uint MeshOffset = 0,
    uint MeshCount = 0,
    GpuAcceleration Acceleration = default,
    BottomLevelDescription? AccelerationDescription = null
);
