using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering.View;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;

namespace Magic.DeferredRendering.World;

/// <summary>
/// The GPU resources the gem draws with, each read once on its first ask: a texture asset by its handle, read and asked
/// of the graph with every level and the sampler its asset asks for, then copied up in the frame after it arrives; a
/// material asset by its handle, read once, with the textures it names asked for and its shader's struct reflected,
/// and handed out as the row a slot's params make over its own, packed as that struct lays them out; a model asset by
/// its handle, made by the preprocessor on a worker, settled into the pools of vertices and indices, the mesh table
/// on a GPU that traces rays and the model table at the start of the next frame, and copied up in it. The pools and
/// the textures are asked of the graph every frame, so the GPU provider keeps what they hold. Each pool hands out ranges
/// first fit from a free list, and the vertex and index pools grow when none holds a model. A model not asked for in the
/// seconds it is kept, and held by no renderer drawn, is evicted: its ranges go back to the free lists and its row of
/// the model table is emptied, and the next ask makes it again. A smoothly filtered texture is sampled with
/// <paramref name="anisotropy"/>, and goes up without its <paramref name="droppedLevels"/> largest mip levels.
/// </summary>
internal sealed class GpuResources(
    ModelPreprocessor models,
    AssetManager assets,
    IShaderCompiler shaders,
    Threads threads,
    int anisotropy,
    int droppedLevels
)
{
    private const int ModelCapacity = 4096;

    private const string FailureShader = "Shaders/Material/Failure.slang";

    private const int MeshCapacity = 1 << 16;

    private readonly Dictionary<ulong, TextureEntry> _textures = [];

    /// <summary>
    /// Each material asked for, by its id, null while it loads; one that did not load is the failure material.
    /// </summary>
    private readonly Dictionary<ulong, Material?> _materials = [];

    private readonly List<(Texture Asset, int First, TextureEntry Entry)> _arrivedTextures = [];

    /// <summary>
    /// Each material shader's struct as reflection lays it out, by the shader's id, null while it is reflected; its
    /// fields are default when it was not, and its materials are drawn as the failure.
    /// </summary>
    private readonly Dictionary<ulong, MaterialLayout?> _layouts = [];

    private readonly List<ShaderTypeArgument> _materialShaders = [];

    // The models' indices; row 0 is none, so it is taken from the start.
    private readonly FreeList _modelRows = new(ModelCapacity, 1);

    /// <summary>
    /// Each resident model by its model index, none where no model is.
    /// </summary>
    private readonly ModelPlace[] _models = new ModelPlace[ModelCapacity];

    /// <summary>
    /// Each model asked for and not evicted, by its id.
    /// </summary>
    private readonly Dictionary<ulong, ModelEntry> _modelEntries = [];

    /// <summary>
    /// The models made since the last frame, by their id with their name, which the next <see cref="Prepare"/> settles.
    /// </summary>
    private readonly List<(ulong Model, string Name, MeshDerived Made, ModelEntry Entry)> _madeModels = [];

    // 2 MiB of vertices and 1 MiB of indices at the start; each grows with the scene.
    private readonly BufferPool _vertices = new(new() { Bytes = 0, Name = "Vertex pool" }, GpuVertex.Bytes, 1 << 16);

    private readonly BufferPool _indices = new(new() { Bytes = 0, Name = "Index pool" }, sizeof(uint), 1 << 18);

    private readonly FreeList _meshRanges = new(MeshCapacity);

    /// <summary>
    /// How many times models were made or failed, ever: when it changes, a renderer waiting for its model may draw now,
    /// and every row is built again.
    /// </summary>
    public int SettledModels { get; private set; }

    public GpuBuffer VertexPool { get; private set; }

    public GpuBuffer IndexPool { get; private set; }

    /// <summary>
    /// Every resident model's meshes as rays see them, a <see cref="GpuMesh"/> a row, from its
    /// <see cref="ModelPlace.MeshOffset"/> in the order of its <see cref="ModelPlace.Meshes"/>; none on a GPU that
    /// traces no rays.
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
    /// Whether a material shader read so far draws translucent: the view keeps its glass targets while one does.
    /// </summary>
    public bool HasTranslucent
    {
        get
        {
            foreach (MaterialLayout? layout in _layouts.Values)
            {
                if (layout is { IsTranslucent: true })
                    return true;
            }

            return false;
        }
    }

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
            if (entry.State == GpuState.Ready)
                textures.Add(entry.Texture);
        }

        return textures.ToImmutable();
    }

    /// <summary>
    /// The pools, with the models made since the last frame settled into them, asked for every frame before any
    /// renderer's row is built, so what they hold stays. The models take their ranges first, so a pool they grow is
    /// asked for at its new size before they are copied into it.
    /// </summary>
    public Result Prepare(RenderGraph graph)
    {
        // The mesh table first, on a GPU that traces rays, so a model placed takes a range of it only there.
        if (graph.Features.HasRayQuery)
        {
            Result<GpuBuffer> meshes = graph.Buffer(
                new() { Bytes = MeshCapacity * GpuMesh.Bytes, Name = "Mesh table" }
            );
            if (!meshes.Ok)
                return Result.Failure(meshes.Error);

            Meshes = meshes.Value;
        }

        // Each model made takes ranges of the pools, which grow to hold it, and of the mesh table, an index and its row
        // of the model table; the failure model when the model table is full. One placed in a frame whose pools failed
        // is placed already.
        foreach ((ulong model, string name, MeshDerived made, ModelEntry entry) in _madeModels)
        {
            if (entry.State != GpuState.Loading)
                continue;

            SettledModels++;
            if (!_modelRows.CanAllocate(1))
            {
                Log.Error($"{name} is drawn as the failure model: the model table is full.");
                entry.State = GpuState.Failed;

                continue;
            }

            ResidentModel where = new(
                (uint)_vertices.Allocate(made.Vertices.Length),
                (uint)made.Vertices.Length,
                (uint)_indices.Allocate(made.Indices.Length),
                (uint)made.Indices.Length,
                made.BoundsCenter,
                made.BoundsRadius,
                (uint)_modelRows.Allocate(1)
            );

            // Its meshes as rays see them, leaving out a mesh with no triangles, take a range of the mesh table on a
            // GPU that traces rays and while one holds them; with none, rays pass through it.
            ImmutableArray<(GpuMesh Mesh, uint Triangles)>.Builder meshes = ImmutableArray.CreateBuilder<(
                GpuMesh Mesh,
                uint Triangles
            )>();
            for (int i = 0; i < made.Meshes.Length && !Meshes.IsNone; i++)
            {
                GpuMesh mesh = made.Meshes[i];
                uint end = i + 1 < made.Meshes.Length ? made.Meshes[i + 1].IndexOffset : (uint)made.Indices.Length;
                uint triangles = (end - mesh.IndexOffset) / 3;
                if (triangles > 0)
                    meshes.Add((mesh with { IndexOffset = where.IndexOffset + mesh.IndexOffset }, triangles));
            }

            if (!_meshRanges.CanAllocate(meshes.Count))
                meshes.Clear();

            // An arrival is a use, so a model that took longer to make than it may go unused is copied up before it
            // can be evicted.
            entry.Asked = Environment.TickCount64;
            entry.Where = where;
            entry.State = GpuState.Ready;
            _models[where.ModelIndex] = new ModelPlace(
                new Handle<Model>(model),
                where.VertexOffset,
                where.VertexCount,
                made.BoundsMin,
                made.BoundsMax,
                meshes.Count == 0 ? 0 : (uint)_meshRanges.Allocate(meshes.Count),
                meshes.ToImmutable()
            );
            Log.Info($"{name}: {made.Vertices.Length} vertices, {made.Indices.Length / 3} triangles.");
        }

        Result<GpuBuffer> vertices = _vertices.GetBuffer(graph);
        if (!vertices.Ok)
            return Result.Failure(vertices.Error);

        VertexPool = vertices.Value;
        Result<GpuBuffer> indices = _indices.GetBuffer(graph);
        if (!indices.Ok)
            return Result.Failure(indices.Error);

        IndexPool = indices.Value;

        // Each placed model copied up, now that the pools are asked for at their size, with its meshes into the mesh
        // table where it has a range of it.
        foreach ((_, _, MeshDerived made, ModelEntry entry) in _madeModels)
        {
            if (entry.State != GpuState.Ready)
                continue;

            ResidentModel where = entry.Where;
            graph.Upload(VertexPool, made.Vertices.AsSpan(), (ulong)where.VertexOffset * GpuVertex.Bytes);

            // Indices point into the whole pool.
            Span<uint> rebased = graph.Upload<uint>(
                IndexPool,
                made.Indices.Length,
                (ulong)where.IndexOffset * sizeof(uint)
            );
            for (int i = 0; i < made.Indices.Length; i++)
            {
                rebased[i] = made.Indices[i] + where.VertexOffset;
            }

            ModelPlace place = _models[where.ModelIndex];
            if (place.Meshes.IsEmpty)
                continue;

            Span<GpuMesh> rows = graph.Upload<GpuMesh>(
                Meshes,
                place.Meshes.Length,
                (ulong)place.MeshOffset * GpuMesh.Bytes
            );
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = place.Meshes[i].Mesh;
            }
        }

        _madeModels.Clear();

        return Result.Success();
    }

    /// <summary>
    /// Evicts every model not asked for in the <paramref name="keepSec"/> before <paramref name="frameStart"/>, the tick
    /// the frame started at, that none of <paramref name="drawn"/> holds: its ranges go back to the pools' free lists,
    /// its range of the mesh table too, and its row of the model table is emptied. A model asked for this frame stays,
    /// whatever the keep time.
    /// Renderers not written since their rows were built ask for nothing, so once a model looks unused each model
    /// <paramref name="drawn"/> holds is asked for first.
    /// </summary>
    public void EvictUnusedModels(ReadOnlySpan<Renderer> drawn, long frameStart, int keepSec)
    {
        long before = frameStart - (keepSec * 1000L);
        bool hasUnused = false;
        foreach (ModelEntry entry in _modelEntries.Values)
        {
            hasUnused |= entry.State == GpuState.Ready && entry.Asked < before;
        }

        if (!hasUnused)
            return;

        foreach (Renderer renderer in drawn)
        {
            if (renderer.Model != Handle<Model>.None)
                Get(renderer.Model);
        }

        foreach ((ulong id, ModelEntry entry) in _modelEntries)
        {
            if (entry.State != GpuState.Ready || entry.Asked >= before)
                continue;

            ResidentModel where = entry.Where;
            ModelPlace place = _models[where.ModelIndex];
            _vertices.Free((int)where.VertexOffset, (int)where.VertexCount);
            _indices.Free((int)where.IndexOffset, (int)where.IndexCount);
            if (!place.Meshes.IsEmpty)
                _meshRanges.Free((int)place.MeshOffset, place.Meshes.Length);

            _modelRows.Free((int)where.ModelIndex, 1);
            _models[where.ModelIndex] = default;
            _modelEntries.Remove(id);
            Log.Info(
                $"{assets.GetPath(new Handle<Model>(id)) ?? $"model {id}"} left the GPU: nothing asked for it in {keepSec} s."
            );
        }
    }

    /// <summary>
    /// Whether the texture is on the GPU, and its heap ids when it is; the first ask starts reading it.
    /// </summary>
    public GpuState Get(Handle<Texture> handle, out uint texture, out uint sampler)
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
    public GpuState Get(Handle<Material> handle) => Get(new MaterialInstance { Handle = handle }, out _);

    /// <summary>
    /// Whether the slot's material, its shader's struct and the textures it names are on the GPU, and the row the slot's
    /// params over the material's make: each field of the struct by its name, a texture's heap id in a <c>TextureRef</c>
    /// and its sampler's in the <c>SamplerRef</c> named for it with "Sampler" after, and 0 where neither sets one. Failed
    /// when the material, its shader's struct or a texture did not load, or the material is the failure. The first ask
    /// starts loading the material. The row names its shader by its place in <see cref="MaterialShaders"/>, which it
    /// takes on the first row that names it.
    /// </summary>
    public GpuState Get(in MaterialInstance slot, out GpuMaterial row)
    {
        GpuState state = Pack(slot, out row, out MaterialLayout layout);
        if (!layout.Fields.IsDefault)
            row.Shader = GetMaterialShader(layout.Shader) | (layout.IsTranslucent ? GpuMaterial.TranslucentBit : 0);

        return state;
    }

    /// <summary>
    /// Whether the slot's material, its shader's struct and the textures it names are on the GPU, its params packed into
    /// the row, and its shader's struct as reflection laid it out; the default struct while it loads.
    /// </summary>
    public GpuState Pack(in MaterialInstance slot, out GpuMaterial row, out MaterialLayout layout)
    {
        row = default;
        layout = default;
        if (!_materials.TryGetValue(slot.Handle.Id, out Material? asked))
        {
            _materials[slot.Handle.Id] = null;
            LoadAsync(slot.Handle).FireAndForget();

            return GpuState.Loading;
        }

        if (asked is not { } material || _layouts.GetValueOrDefault(material.Shader.Id) is not { } reflected)
            return GpuState.Loading;

        layout = reflected;
        if (layout.Fields.IsDefault)
            return GpuState.Failed;

        MaterialOverrides overrides = slot.Overrides;
        GpuState state = material.Shader == assets.Find<Shader>(FailureShader) ? GpuState.Failed : GpuState.Ready;
        Span<uint> words = row.Params;
        foreach (ShaderField field in layout.Fields)
        {
            if (!TryGetParam(field.Name, out MaterialParam? param))
                continue;

            Span<uint> into = words.Slice((int)field.Offset / 4, (int)field.Size / 4);
            if (field.Struct == "TextureRef")
            {
                GpuState texture = PackTexture(layout, field.Name, param.Texture, words);
                state = texture == GpuState.Ready || state == GpuState.Failed ? state : texture;

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
        if (HasFailed(model))
        {
            Handle<Model> failure = assets.GetFailure<Model, Model>();
            if (failure == Handle<Model>.None || HasFailed(failure))
                return null;

            return Get(failure) is { } stand ? stand with { IsFailure = true } : null;
        }

        if (!_modelEntries.TryGetValue(model.Id, out ModelEntry? entry))
        {
            entry = new ModelEntry();
            _modelEntries[model.Id] = entry;
            LoadAsync(model, entry).FireAndForget();
        }

        entry.Asked = Environment.TickCount64;

        return entry.State == GpuState.Ready ? entry.Where : null;
    }

    /// <summary>
    /// Whether the model is in the pools, copied up in this frame's transfers at the latest, or could not be made and the
    /// failure model it is drawn as has arrived in its place, or has no place to arrive in.
    /// </summary>
    public bool HasArrived(Handle<Model> model)
    {
        if (_modelEntries.GetValueOrDefault(model.Id)?.State == GpuState.Ready)
            return true;

        if (!HasFailed(model))
            return false;

        Handle<Model> failure = assets.GetFailure<Model, Model>();

        return failure == Handle<Model>.None || failure == model || HasArrived(failure);
    }

    /// <summary>
    /// Whether the model could not be made, and is drawn as the failure model.
    /// </summary>
    public bool HasFailed(Handle<Model> model) => _modelEntries.GetValueOrDefault(model.Id)?.State == GpuState.Failed;

    /// <summary>
    /// Asks for every texture read and its sampler, and copies the levels of the textures that arrived, in the frame's
    /// upload pass.
    /// </summary>
    public void Upload(RenderGraph graph)
    {
        // Each texture read and its sampler asked for, and ready with their heap ids; failed, and drawn as the failure,
        // when either was not made.
        foreach (TextureEntry entry in _textures.Values)
        {
            if (entry.State == GpuState.Failed || entry.Description is not { } description)
                continue;

            Result<GpuTexture> texture = graph.Texture(description);
            Result<GpuSampler> sampler = graph.Sampler(entry.SamplerDescription);
            Result made = !texture.Ok ? texture : sampler;
            if (!made.Ok)
            {
                Log.Error($"{description.Name} is drawn as a failure: {made.Error}");
                entry.State = GpuState.Failed;

                continue;
            }

            entry.Texture = texture.Value;
            entry.Sampler = sampler.Value;
            entry.State = GpuState.Ready;
        }

        foreach ((Texture asset, int first, TextureEntry entry) in _arrivedTextures)
        {
            if (entry.State != GpuState.Ready)
                continue;

            for (int level = first; level < asset.Levels.Length; level++)
            {
                TextureLevel source = asset.Levels[level];
                graph.Upload(entry.Texture, asset.Pixels.Span.Slice(source.Offset, source.Size), level - first);
            }
        }

        _arrivedTextures.Clear();
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
    private GpuState PackTexture(MaterialLayout layout, string name, Handle<Texture> texture, Span<uint> words)
    {
        if (texture == Handle<Texture>.None)
            return GpuState.Ready;

        GpuState state = Get(texture, out uint id, out uint sampler);
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
        Result<long> levels = await shaders
            .ReflectConstantAsync(type, "Levels", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> history = await shaders
            .ReflectConstantAsync(type, "History", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> jitters = await shaders
            .ReflectConstantAsync(type, "Jitters", CancellationToken.None)
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
            _layouts[shader.Id] = new MaterialLayout(default, type, false, default);

            return;
        }

        _layouts[shader.Id] = new MaterialLayout(
            fields.Value,
            type,
            translucent.Ok && translucent.Value != 0,
            new EffectNeeds(
                levels.Ok ? (int)levels.Value : 0,
                history.Ok && history.Value != 0,
                jitters.Ok && jitters.Value != 0
            )
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
            entry.State = GpuState.Failed;

            return;
        }

        // The next upload asks for the texture and its sampler and copies its levels in, from the first one kept.
        Texture asset = loaded.Value;
        int first = Math.Clamp(droppedLevels, 0, asset.Levels.Length - 1);
        entry.Description = new TextureDescription
        {
            Width = asset.Levels[first].Width,
            Height = asset.Levels[first].Height,
            Format = ToGpu(asset.Format),
            Usage = TextureUsages.Sampled | TextureUsages.CopyDestination,
            Levels = asset.Levels.Length - first,
            Name = name,
        };
        entry.SamplerDescription = new SamplerDescription
        {
            Filter = asset.Filter,
            Wrap = asset.Wrap,
            Anisotropy = asset.Filter == TextureFilter.Linear ? anisotropy : 1,
        };
        _arrivedTextures.Add((asset, first, entry));
    }

    private async Task LoadAsync(Handle<Model> model, ModelEntry entry)
    {
        Result<MeshDerived> derived = await assets
            .LoadPreprocessedAsync(model, models, CancellationToken.None)
            .ConfigureAwait(false);

        await threads.Enter(EngineThread.Render);
        string name = assets.GetPath(model) ?? $"model {model.Id}";
        if (!derived.Ok)
        {
            Log.Error($"{name} is drawn as the failure model: {derived.Error}");
            entry.State = GpuState.Failed;
            SettledModels++;

            return;
        }

        _madeModels.Add((model.Id, name, derived.Value, entry));
    }

    /// <summary>
    /// A material shader's struct as its rows are packed: its fields as reflection lays them out, default when it could
    /// not be, the struct a pass is specialised to, and, for an effect's, what it declares it needs.
    /// </summary>
    public readonly record struct MaterialLayout(
        ImmutableArray<ShaderField> Fields,
        ShaderTypeArgument Shader,
        bool IsTranslucent,
        EffectNeeds Needs
    );

    /// <summary>
    /// A model asked for: where it is in the loading, when it was last asked, in
    /// <see cref="Environment.TickCount64"/> milliseconds, and where it is in the pools once it is resident.
    /// </summary>
    private sealed class ModelEntry
    {
        public GpuState State { get; set; }

        public long Asked { get; set; }

        public ResidentModel Where { get; set; }
    }

    /// <summary>
    /// A texture asked for: where it is in the loading, what it and its sampler are made to once it is read, and their
    /// heap ids once they are made.
    /// </summary>
    private sealed class TextureEntry
    {
        public GpuState State { get; set; }

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
    Vector3 BoundsCenter,
    float BoundsRadius,
    uint ModelIndex,
    bool IsFailure = false
);
