using System.Diagnostics.Metrics;
using System.IO;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// A card of a packed model: the card its atlases are read from, and its row but for their refs.
/// </summary>
internal readonly record struct PackedCard(Card Card, GpuCard Row);

/// <summary>
/// What a material is drawn with: its row, the surface and variant its pipeline is composed of, and how it blends; or,
/// when its shader is a sky, the sky shader the sky draw is composed of, which no mesh draws.
/// </summary>
internal readonly record struct DrawnMaterial(int Row, SurfaceKey Surface, MaterialType Type, bool IsSky);

/// <summary>
/// A surface shader and the variant it is compiled as.
/// </summary>
internal readonly record struct SurfaceKey(Handle<Shader> Shader, bool Masked, bool DoubleSided);

/// <summary>
/// Every asset resident on the GPU: models, as clusters in one pool; the textures materials sample and cards show, in
/// texture pools; materials, as rows; each model's distance field, in one atlas; and the textures cameras draw into.
/// What is kept is what is held: <see cref="Record(Handle{Model})"/> and its overloads hold an asset, starting its load
/// the first time, and hand back where it is; <see cref="Release(Handle{Model})"/> lets one hold go, and
/// <see cref="Purge"/> lets go of whatever nothing holds any more, once a frame, so an asset let go and held again in
/// one frame never leaves. A material holds the textures it samples and a model the atlases its cards show, so a
/// texture stays while anything shows it. Each asset loads on a worker, is made ready for the GPU there
/// (<see cref="GpuConverter"/>), and is taken in on the render thread as <see cref="Take()"/> runs; one whose file
/// changes loads and is taken in again in place of what it had, and one that does not load is drawn as the failure
/// (<see cref="Assets.Failure{T}"/>). The failure model, the default material and the failure materials are held from
/// the start and never leave. Render thread.
/// <para>
/// A model's row in <c>Models</c> is made the first time it is held and kept while it is, so an instance's row never
/// changes as its model comes in; its clusters, levels and cards go in their tables and its vertices and triangles in
/// one buffer of words every draw reads (<c>Geometry</c>). Models are taken in the nearest to the player first and no
/// more a frame than <see cref="ClusterRenderingSettings.ModelUploadRate"/>, so a burst of models streaming in never
/// stalls a frame. A model with no clusters of its own (<see cref="Model.Clusters"/>), as one a gem made, is split into
/// clusters of one level as it is.
/// </para>
/// <para>
/// Textures are in eight texture arrays every surface pipeline binds at once, since a GPU without bindless textures
/// cannot pick one by an index: colour (sRGB) or data (linear), each 256, 512, 1024 or 2048 texels square, with its
/// mips down to a 4 x 4 block, compressed to BC7, a quarter of the memory and of the reads of eight bits a channel. A
/// texture is resampled into the smallest that holds it (2048 for anything bigger), compressed and kept in the cache,
/// so it is compressed once, and takes a layer of it; a surface names it by <c>TextureRef</c>,
/// <c>(pool &lt;&lt; 16) | layer</c> (Shaders/Include/SurfaceBindings.hlsli). A pool grows by doubling its layers,
/// copying the old on the GPU, so a layer that copy will write is not uploaded into until the frame after.
/// </para>
/// <para>
/// A material is a row of <c>Materials</c> its surface's generated <c>LoadMaterialParams</c> reads,
/// packed by its surface shader's params, which are read once. Its row also says how it is drawn
/// (<c>MaterialBins</c>): the raster bin its clusters are drawn into the visibility buffer by when they
/// clip (a masked surface's, or a card's or a fade's of any), the resolve bin its pixels are shaded into the G-buffer
/// by, and whether it is in yet, transparent, masked or double-sided. What is broken is packed, in its own row, as the
/// failure material for what broke. A material is drawn only once it can be drawn as it should be: its textures in, and
/// every pipeline it is drawn with compiled (<see cref="PassGraph.IsDrawable"/>). Until then its row stays as it was,
/// not in for a new one, so nothing is drawn flat, black or not at all for a while and then changes.
/// </para>
/// <para>
/// A model's distance field, which the world's field for global illumination is made from, is a box of
/// <see cref="FieldResolution"/> texels a side around the model at its least detail, each texel how far its middle is
/// from the nearest surface, negative inside. It is made on a worker as its model is taken in, kept in the cache, and
/// packed in one volume, a brick each, which the model's row names (<see cref="GpuModel.Field"/>).
/// </para>
/// </summary>
internal sealed class GpuCache
{
    internal const int TexturePoolCount = 8;

    /// <summary>
    /// Texels a side of a model's field.
    /// </summary>
    internal const int FieldResolution = 32;

    /// <summary>
    /// Bricks the field atlas holds, slot 0 for none among them: the rows of <c>MeshFields</c>.
    /// </summary>
    internal const int FieldSlots = FieldsAcross * FieldsAcross * FieldsDeep;

    /// <summary>
    /// A row's <c>MaterialBins</c> word: its raster bin in the low byte, its resolve bin in the third,
    /// and these.
    /// </summary>
    private const uint BinMasked = 1u << 8;
    private const uint BinDoubleSided = 1u << 9;
    private const uint BinTransparent = 1u << 10;
    private const uint BinReady = 1u << 11;

    /// <summary>
    /// Texels a side of a texture pool's smallest mip: a block of BC7, as a level smaller than a block cannot be copied
    /// when a pool grows.
    /// </summary>
    private const int SmallestMip = 4;

    /// <summary>
    /// Models whose distance fields are held at once, 64 KB each.
    /// </summary>
    private const int FieldCapacity = 1024;

    /// <summary>
    /// Bricks along the field atlas's width and height; it is as many bricks deep as its capacity needs.
    /// </summary>
    private const int FieldsAcross = 8;

    private const int FieldsDeep = (FieldCapacity + (FieldsAcross * FieldsAcross) - 1) / (FieldsAcross * FieldsAcross);

    /// <summary>
    /// The most resolve bins there are, as a byte numbers them.
    /// </summary>
    private const int MaxBins = 256;

    private static readonly Counter<long> _uploaded = ClusterRenderingGem.Meter.CreateCounter<long>(
        "magic.gems.cluster_rendering.pool.upload.size",
        "By",
        "Bytes of models taken into the pool.");
    private static readonly Gauge<int> _heldModelCount = ClusterRenderingGem.Meter
        .CreateGauge<int>("magic.gems.cluster_rendering.pool.models", "{model}", "Models held in the pool.");

    /// <summary>
    /// The texture pools' sizes, texels a side, colour and data alike.
    /// </summary>
    private static readonly int[] _poolSizes = [256, 512, 1024, 2048];

    private readonly GpuResources _resources;

    /// <summary>
    /// The rows the cache writes: a model's, its levels', clusters' and cards'; a material's, what it gives global
    /// illumination and how it is drawn; and each model's field's.
    /// </summary>
    private readonly GpuTable<GpuModel> _modelTable;
    private readonly GpuTable<GpuLevel> _levelTable;
    private readonly GpuTable<GpuCluster> _clusterTable;
    private readonly GpuTable<GpuCard> _cardTable;
    private readonly GpuTable<GpuMaterial> _materialTable;
    private readonly GpuTable<GpuMaterialGi> _materialGiTable;
    private readonly GpuTable<uint> _binTable;
    private readonly GpuTable<GpuMeshField> _fieldTable;
    private readonly Assets _assets;
    private readonly Caches _caches;
    private readonly PassGraph _graph;
    private readonly ClusterRenderingSettings _settings;
    private readonly CancellationToken _stopping;

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

    /// <summary>
    /// Each model's field read or made on a worker, for the packing it was made from, for the render thread to take in.
    /// </summary>
    private readonly Channel<(ModelEntry Entry, PackedModel Packed, ModelField? Field)> _fieldsMade =
        Channel.CreateUnbounded<(ModelEntry Entry, PackedModel Packed, ModelField? Field)>(
            new UnboundedChannelOptions { SingleReader = true });

    private readonly Resident<Texture, TextureEntry, PreparedTexture> _textures;

    /// <summary>
    /// The textures taken in, or failed, this frame, until it ends: whose models' cards and materials are worked out
    /// anew.
    /// </summary>
    private readonly HashSet<Handle<Texture>> _changedTextures = [];
    private readonly TexturePool[] _pools = new TexturePool[TexturePoolCount];

    /// <summary>
    /// Uploads held back a frame, as they write layers a pool's growing copies this frame.
    /// </summary>
    private readonly List<(Handle<Texture> Texture, byte[][] Levels)> _heldBack = [];

    /// <summary>
    /// What a texture pool's binding stands in for while it holds nothing: one white texel.
    /// </summary>
    private readonly GpuTexture _emptyPool;

    private readonly Resident<Material, MaterialEntry, Material> _materials;
    private readonly Dictionary<Handle<Shader>, SurfaceEntry> _surfaces = [];

    /// <summary>
    /// Surfaces read, or that could not be, for the render thread to take in as <see cref="Take()"/> starts.
    /// </summary>
    private readonly Channel<(Handle<Shader> Surface, SurfaceSource? Source)> _surfacesRead =
        Channel.CreateUnbounded<(Handle<Shader> Surface, SurfaceSource? Source)>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The materials packed and waiting for their pipelines to be drawn (<see cref="MaterialEntry.Waiting"/>), or let
    /// go since: those are passed over.
    /// </summary>
    private readonly List<Handle<Material>> _waiting = [];

    private readonly Dictionary<SurfaceKey, int> _rasterBins = [];
    private readonly Dictionary<SurfaceKey, int> _resolveBins = [];
    private readonly RangeAllocator _materialRows = new();
    private readonly Handle<Material> _defaultMaterial;

    /// <summary>
    /// What stands in for what broke, and where a failure is said.
    /// </summary>
    private readonly FailureHandler _failures;

    /// <summary>
    /// The surfaces that broke since the materials last asked, whose materials are packed anew.
    /// </summary>
    private readonly HashSet<Handle<Shader>> _broke = [];

    /// <summary>
    /// The field atlas's bricks, by model, and those free.
    /// </summary>
    private readonly Dictionary<Handle<Model>, int> _fieldSlots = [];
    private readonly Stack<int> _freeFieldSlots = new();

    private readonly Resident<RenderTexture, RenderTextureEntry, RenderTexture> _renderTextures;

    /// <summary>
    /// Every field, a brick of <see cref="FieldResolution"/> texels a side each, in rows of <see cref="FieldsAcross"/>
    /// both ways: the frame's <c>MeshFieldAtlas</c>.
    /// </summary>
    private readonly GpuTexture _fieldAtlas;

    private int _wordCapacity = 1 << 22;

    /// <summary>
    /// Every model's vertices and triangles, as words: the frame's <c>Geometry</c>.
    /// </summary>
    private GpuBuffer _geometry;

    /// <summary>
    /// The sampler every texture pool is read with, as many taps as the settings say.
    /// </summary>
    private GpuSampler _sampler;

    /// <summary>
    /// The taps the texture sampler filters with at a grazing angle, 1 to 16.
    /// </summary>
    private float _anisotropy;

    /// <summary>
    /// The world's sky material, held while it is the world's.
    /// </summary>
    private Handle<Material> _sky;

    internal GpuCache(
        GpuResources resources,
        Assets assets,
        Caches caches,
        PassGraph graph,
        FailureHandler failures,
        ClusterRenderingSettings settings,
        CancellationToken stopping)
    {
        _resources = resources;
        _modelTable = resources.MakeTable<GpuModel>(GpuContract.Models, 256);
        _levelTable = resources.MakeTable<GpuLevel>(GpuContract.Levels, 1024);
        _clusterTable = resources.MakeTable<GpuCluster>(GpuContract.Clusters, 4096);
        _cardTable = resources.MakeTable<GpuCard>(GpuContract.Cards, 256);
        _materialTable = resources.MakeTable<GpuMaterial>(GpuContract.Materials, 64);
        _materialGiTable = resources.MakeTable<GpuMaterialGi>(GpuContract.MaterialGi, 64);
        _binTable = resources.MakeTable<uint>(GpuContract.MaterialBins, 64);
        _fieldTable = resources.MakeTable<GpuMeshField>(GpuContract.MeshFields, FieldSlots);
        _assets = assets;
        _caches = caches;
        _graph = graph;
        _failures = failures;
        _settings = settings;
        _stopping = stopping;
        _models = new(
            assets,
            "A model does not load, so what draws it is drawn as the failure",
            model => Task.Run(() => GpuConverter.Convert(model), stopping),
            stopping);
        _textures = new(
            assets,
            "A material's texture does not load, so its material is drawn as the failure",
            PrepareAsync,
            stopping);
        _materials = new(assets, "A material does not load, so it is drawn as the failure", Task.FromResult, stopping);
        _renderTextures = new(
            assets,
            "A camera's render texture does not load, so it draws nothing",
            Task.FromResult,
            stopping);
        _geometry = resources.MakeBuffer(GpuContract.Geometry, (uint)_wordCapacity * sizeof(uint));
        for (int i = 0; i < TexturePoolCount; i++)
        {
            _pools[i] = new TexturePool((uint)_poolSizes[i % 4]);
        }

        _emptyPool = resources.MakeTexture(new GpuTextureDescription(
            GpuFormat.Rgba8Unorm,
            GpuTextureUsage.Sampled,
            1,
            1,
            1,
            1,
            GpuTextureKind.Texture2DArray));
        resources.Upload(new GpuTextureRegion(_emptyPool), [255, 255, 255, 255]);
        _anisotropy = Math.Clamp(settings.Anisotropy, 1, 16);
        _sampler = resources.GetSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Repeat, _anisotropy));
        BindPools();
        _fieldAtlas = resources.MakeTexture(
            GpuContract.MeshFieldAtlas,
            FieldsAcross * FieldResolution,
            FieldsAcross * FieldResolution,
            1,
            FieldsDeep * FieldResolution);

        // Slot 0 stands for none, as a mesh row with no field holds.
        for (int slot = FieldSlots - 1; slot >= 1; slot--)
        {
            _freeFieldSlots.Push(slot);
        }

        Record(failures.Model);
        _defaultMaterial = assets.Default<Material>();
        Record(_defaultMaterial);
        foreach (Handle<Material> failure in failures.Materials)
        {
            Record(failure);
        }
    }

    /// <summary>
    /// The models taken in, again or for the first time, failed, or whose cards' atlases or field came in, since the
    /// instances last asked: whose instances can be drawn now, or are drawn anew.
    /// </summary>
    internal HashSet<Handle<Model>> ChangedModels { get; } = [];

    /// <summary>
    /// The materials whose row, surface or type changed since the instances last asked: whose instances draw anew.
    /// </summary>
    internal HashSet<Handle<Material>> ChangedMaterials { get; } = [];

    /// <summary>
    /// The surfaces with a raster bin of their own, from <see cref="GpuContract.FixedRasterBins"/> on, in bin order:
    /// what their clusters clip by when they are masked, cards or fading.
    /// </summary>
    internal List<SurfaceKey> RasterBins { get; } = [];

    /// <summary>
    /// The surfaces their pixels are shaded with, in resolve bin order: a surface and whether it is double-sided.
    /// </summary>
    internal List<SurfaceKey> ResolveBins { get; } = [];

    /// <summary>
    /// The transparent surfaces drawn forward.
    /// </summary>
    internal HashSet<SurfaceKey> TransparentSurfaces { get; } = [];

    /// <summary>
    /// Holds <paramref name="model"/>, starting its load the first time: its row, which never changes while it is held.
    /// </summary>
    internal int Record(Handle<Model> model)
    {
        return _models.Record(model, () =>
        {
            ModelEntry entry = new(model, _modelRows.Allocate(1));
            _modelTable.Set(entry.Row, default);
            return entry;
        }).Row;
    }

    /// <summary>
    /// Holds <paramref name="texture"/>, starting its load the first time: its <c>TextureRef</c>,
    /// <see cref="GpuContract.NoTexture"/> while it loads. Whether it failed is
    /// <see cref="HasFailed(Handle{Texture})"/>.
    /// </summary>
    internal uint Record(Handle<Texture> texture)
    {
        if (texture == Handle<Texture>.None) return GpuContract.NoTexture;

        return _textures.Record(texture, static () => new TextureEntry()).Ref;
    }

    /// <summary>
    /// Holds <paramref name="material"/>, none being the default material, starting its load the first time: its row,
    /// whose bins say when it is in.
    /// </summary>
    internal int Record(Handle<Material> material)
    {
        if (material == Handle<Material>.None) material = _defaultMaterial;

        return _materials.Record(material, () =>
        {
            MaterialEntry entry = new(_materialRows.Allocate(1));
            _binTable.Set(entry.Row, 0);
            return entry;
        }).Row;
    }

    /// <summary>
    /// Holds a camera's render texture, starting its load the first time; it is drawn into once
    /// <see cref="GetTexture(Handle{RenderTexture})"/> has it.
    /// </summary>
    internal void Record(Handle<RenderTexture> texture)
    {
        _renderTextures.Record(texture, static () => new RenderTextureEntry());
    }

    /// <summary>
    /// Lets one hold of <paramref name="model"/> go: once none holds it, <see cref="Purge"/> lets it leave.
    /// </summary>
    internal void Release(Handle<Model> model) => _models.Release(model);

    internal void Release(Handle<Texture> texture)
    {
        if (texture != Handle<Texture>.None) _textures.Release(texture);
    }

    internal void Release(Handle<Material> material)
        => _materials.Release(material == Handle<Material>.None ? _defaultMaterial : material);

    internal void Release(Handle<RenderTexture> texture) => _renderTextures.Release(texture);

    /// <summary>
    /// Lets go of every asset nothing holds any more, so what is not drawn is not kept: they load again when held
    /// again. Materials go first, then models, as they hold the textures that go last.
    /// </summary>
    internal void Purge()
    {
        _materials.Purge(Evict);
        _models.Purge(Evict);
        _textures.Purge(Evict);
        _renderTextures.Purge(Evict);
    }

    /// <summary>
    /// Takes in what workers finished since the last frame, textures first, as the models' cards and the materials are
    /// worked out from them, then the models and the materials and the render textures; then follows the settings'
    /// filtering.
    /// </summary>
    internal void Take()
    {
        FollowSettings();
        TakeTextures();
        TakeModels();
        TakeMaterials();
        _renderTextures.Take(TakeRenderTexture);
    }

    /// <summary>
    /// Ends the frame: the textures that changed this frame are forgotten, and the uploads held back from a pool's
    /// growing go in now.
    /// </summary>
    internal void EndFrame()
    {
        _changedTextures.Clear();
        foreach (TexturePool pool in _pools)
        {
            pool.HasGrown = false;
        }

        foreach ((Handle<Texture> texture, byte[][] levels) in _heldBack)
        {
            Upload(texture, levels);
        }

        _heldBack.Clear();
    }

    /// <summary>
    /// The model's file changed and it loaded again: it is packed again, and taken in in place of what it had.
    /// </summary>
    internal void Reload(Model model)
    {
        _models.Reload(model);
    }

    /// <summary>
    /// The texture's file changed and it loaded again: it is resampled into the layer it has.
    /// </summary>
    internal void Reload(Texture texture)
    {
        _textures.Reload(texture);
    }

    /// <summary>
    /// The material's file changed and it loaded again.
    /// </summary>
    internal void Reload(Material material)
    {
        _materials.Reload(material);
    }

    /// <summary>
    /// A shader changed: a surface materials use is read again, no longer broken until it fails again, and its
    /// materials packed again once it is (<see cref="TakeSurface"/>).
    /// </summary>
    internal void Reload(Shader shader)
    {
        _failures.Repair(shader.Handle);
        if (!_surfaces.ContainsKey(shader.Handle)) return;

        _surfaces[shader.Handle] = new SurfaceEntry();
        ReadSurfaceAsync(shader.Handle).FireAndForget();
    }

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
    /// The model as it is in the pool, for what draws its clusters itself: its first cluster's row and what was packed;
    /// none until it is in.
    /// </summary>
    internal (int FirstCluster, PackedModel Packed)? FindInPool(Handle<Model> model)
        => _models.Entries.TryGetValue(model, out ModelEntry? entry) && entry.In is { } packed
            ? (entry.FirstCluster, packed)
            : null;

    internal bool HasFailed(Handle<Texture> texture)
        => _textures.Entries.TryGetValue(texture, out TextureEntry? entry) && entry.HasFailed;

    /// <summary>
    /// A held material slot's row, none being the default material; the default material's for one nothing holds.
    /// </summary>
    internal int GetRow(Handle<Material> material)
    {
        Handle<Material> held = material == Handle<Material>.None ? _defaultMaterial : material;
        MaterialEntry found = _materials.Entries.TryGetValue(held, out MaterialEntry? entry)
            ? entry
            : _materials.Entries[_defaultMaterial];
        return found.Row;
    }

    /// <summary>
    /// What a material slot is drawn with, once its material is in; none in the slot is the default material. Null
    /// while it loads, or when nothing holds it.
    /// </summary>
    internal DrawnMaterial? Get(Handle<Material> material)
    {
        Handle<Material> held = material == Handle<Material>.None ? _defaultMaterial : material;
        return _materials.Entries.TryGetValue(held, out MaterialEntry? entry) ? entry.Drawn : null;
    }

    /// <summary>
    /// Whether a held material is drawn: in, its textures in and its pipelines compiled.
    /// </summary>
    internal bool IsShown(Handle<Material> material)
    {
        Handle<Material> held = material == Handle<Material>.None ? _defaultMaterial : material;
        return _materials.Entries.TryGetValue(held, out MaterialEntry? entry)
            && (_binTable.Rows[entry.Row] & BinReady) != 0;
    }

    /// <summary>
    /// Whether a material slot's material is in and is a sky, which draws the world's sky and no mesh: a mesh that
    /// names one is drawn as the shader failure in its place.
    /// </summary>
    internal bool IsSky(Handle<Material> material) => Get(material) is { IsSky: true };

    /// <summary>
    /// Why a material slot is drawn as the failure, in a few words; null when it is not.
    /// </summary>
    internal string? GetFailure(Handle<Material> material)
    {
        Handle<Material> held = material == Handle<Material>.None ? _defaultMaterial : material;
        return _materials.Entries.TryGetValue(held, out MaterialEntry? entry) ? entry.Failure : null;
    }

    /// <summary>
    /// What the sky is drawn with this frame, and the row its params are loaded from: the world's sky material's sky
    /// shader once it is in, the material held while it is the world's; the renderer's own sky without one, or while it
    /// loads; and the sky failure material when the material is no sky, its shader a surface or a sky that does not
    /// compile, as a broken material is drawn as the failure (<see cref="FailureType.Sky"/>). Why is said where
    /// failures are (<see cref="FailureHandler.SayBrokenSky"/>).
    /// </summary>
    internal (SurfaceKey Surface, int Row) GetSky(Handle<Material>? world)
    {
        Handle<Material> material = world ?? Handle<Material>.None;
        if (material != _sky)
        {
            if (material != Handle<Material>.None) Record(material);

            if (_sky != Handle<Material>.None) Release(_sky);

            _sky = material;
        }

        DrawnMaterial? drawn = material == Handle<Material>.None ? null : Get(material);
        Handle<Material> broken = drawn is { IsSky: false } ? material : Handle<Material>.None;
        _failures.SayBrokenSky(
            broken,
            broken == Handle<Material>.None ? string.Empty : GetFailure(broken) ?? "a surface, not a sky");
        if (broken != Handle<Material>.None) drawn = Get(_failures.GetMaterial(FailureType.Sky));

        return drawn is { IsSky: true } sky ? (sky.Surface, sky.Row) : (default, 0);
    }

    /// <summary>
    /// A camera's render texture once it is made; none while it loads, or when it does not.
    /// </summary>
    internal BoundResource GetTexture(Handle<RenderTexture> texture)
        => _renderTextures.Entries.TryGetValue(texture, out RenderTextureEntry? entry) ? entry.Made : default;

    /// <summary>
    /// The pool and layer a <c>TextureRef</c> names: <c>(pool &lt;&lt; 16) | layer</c>.
    /// </summary>
    private static (int Pool, uint Layer) Unpack(uint textureRef) => ((int)(textureRef >> 16), textureRef & 0xFFFF);

    /// <summary>
    /// The pool for a texture: colour or data by its format, and the smallest size that holds it.
    /// </summary>
    private static int GetPool(Texture texture)
    {
        int largest = Math.Max(texture.Width, texture.Height);
        int size = 0;
        while (size < _poolSizes.Length - 1 && _poolSizes[size] < largest)
        {
            size++;
        }

        return (texture.Format == GpuFormat.Rgba8Srgb ? 0 : 4) + size;
    }

    /// <summary>
    /// Mips of a pool of <paramref name="size"/> texels a side, down to <see cref="SmallestMip"/>.
    /// </summary>
    private static int GetMipCount(uint size) => BitOperations.Log2(size) - BitOperations.Log2(SmallestMip) + 1;

    private static bool IsUsingAnyOf(Material material, HashSet<Handle<Texture>> textures)
    {
        foreach (MaterialParam param in material.Params.Values)
        {
            if (textures.Contains(param.Texture)) return true;
        }

        return false;
    }

    /// <summary>
    /// A hash of the triangles a field is made from (<see cref="GpuConverter.Convert(IReadOnlyList{Mesh})"/>).
    /// </summary>
    private static ulong HashOf(IReadOnlyList<Mesh> meshes)
    {
        XxHash3 hash = new();
        foreach (Mesh mesh in meshes)
        {
            hash.Append(MemoryMarshal.AsBytes(mesh.Vertices.Span));
            hash.Append(MemoryMarshal.AsBytes(mesh.Indices.Span));
        }

        ulong value = hash.GetCurrentHashAsUInt64();
        return value == 0 ? 1 : value;
    }

    /// <summary>
    /// <paramref name="texture"/> as a surface names it once it is in; <see cref="GpuContract.NoTexture"/> until then,
    /// or when nothing holds it.
    /// </summary>
    private uint GetRef(Handle<Texture> texture)
        => _textures.Entries.TryGetValue(texture, out TextureEntry? entry) ? entry.Ref : GpuContract.NoTexture;

    /// <summary>
    /// Filters textures with as many taps as the settings now say, 1 to 16, from now on: the pools read through that
    /// sampler when it changed.
    /// </summary>
    private void FollowSettings()
    {
        float clamped = Math.Clamp(_settings.Anisotropy, 1, 16);
        if (clamped == _anisotropy) return;

        _anisotropy = clamped;
        _sampler = _resources.GetSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Repeat, clamped));
        BindPools();
    }

    /// <summary>
    /// Each pool as surfaces read it: its array once it has one, else the white stand-in, through the pools' sampler.
    /// </summary>
    private void BindPools()
    {
        for (int pool = 0; pool < TexturePoolCount; pool++)
        {
            GpuTexture texture = _pools[pool].Texture == GpuTexture.None ? _emptyPool : _pools[pool].Texture;
            _resources.Frame.Set(GpuContract.TexturePools[pool], BoundResource.Of(texture, 0, 0, _sampler));
        }
    }

    /// <summary>
    /// Takes in what workers finished for models since the last frame, puts the models packed in the pool, and writes
    /// the cards whose atlases came in.
    /// </summary>
    private void TakeModels()
    {
        _models.Take(TakeModel);
        while (_fieldsMade.Reader.TryRead(out (ModelEntry Entry, PackedModel Packed, ModelField? Field) made))
        {
            TakeField(made.Entry, made.Packed, made.Field);
        }

        PlaceReady();
        if (_changedTextures.Count > 0) WriteChangedCards();
    }

    /// <summary>
    /// A model packed, or that did not load (none): it waits its turn to go in the pool, or what draws it is drawn as
    /// the failure.
    /// </summary>
    private void TakeModel(Handle<Model> model, ModelEntry entry, PackedModel? packed)
    {
        if (packed is null)
        {
            entry.HasFailed = true;
            ChangedModels.Add(model);
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
        _heldModelCount.Record(_models.Entries.Count);
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

            if (entry.AreCardsIn != wereIn || CanDraw(entry) != wasDrawable) ChangedModels.Add(entry.Handle);
        }
    }

    /// <summary>
    /// The model's field was read or made, for <paramref name="packed"/>: named in its row, and the model counted
    /// changed so what is made from its instances' fields is made again; nothing when the model was let go or packed
    /// again meanwhile.
    /// </summary>
    private void TakeField(ModelEntry entry, PackedModel packed, ModelField? field)
    {
        if (!_models.IsCurrent(entry.Handle, entry) || entry.In != packed) return;

        entry.Field = PlaceField(entry.Handle, field);
        WriteRow(entry);
        ChangedModels.Add(entry.Handle);
    }

    /// <summary>
    /// Puts a packed model in the pool in place of what it had: its words, clusters, levels, cards and row, then starts
    /// its field. Answers the bytes it sent.
    /// </summary>
    private long Place(ModelEntry entry)
    {
        if (entry.Waiting is not { } packed) return 0;

        // Its new cards' atlases are held before the old ones are let go, so an atlas both show never leaves.
        foreach (PackedCard card in packed.Cards)
        {
            Record(card.Card.Surface.Handle);
            Record(card.Card.Normals.Handle);
        }

        Free(entry);
        entry.Waiting = null;
        int words = _words.Allocate(packed.Words.Length);
        if (_words.End > _wordCapacity)
        {
            // Before the model is in: growing uploads again only the models already in, each at its own place.
            GrowGeometry(_words.End);
        }

        entry.In = packed;
        _resources.Upload(_geometry, (uint)words * sizeof(uint), MemoryMarshal.AsBytes(packed.Words.AsSpan()));
        entry.FirstWord = words;
        WriteClusters(entry, packed);
        entry.Field = 0;
        WriteCards(entry);
        WriteRow(entry);
        ChangedModels.Add(entry.Handle);
        MakeFieldAsync(entry, packed).FireAndForget();
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
            _clusterTable.Set(entry.FirstCluster + i, cluster);
        }

        for (int i = 0; i < packed.Levels.Length; i++)
        {
            GpuLevel level = packed.Levels[i];
            level.FirstCluster += (uint)entry.FirstCluster;
            _levelTable.Set(entry.FirstLevel + i, level);
        }
    }

    /// <summary>
    /// The model's row: its cards only once their atlases are all in, as a card without its atlas would be drawn a flat
    /// square; until then its clusters are drawn at every distance.
    /// </summary>
    private void WriteRow(ModelEntry entry)
    {
        if (entry.In is not { } packed) return;

        _modelTable.Set(entry.Row, new GpuModel
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
            uint surface = GetRef(card.Card.Surface.Handle);
            uint normals = GetRef(card.Card.Normals.Handle);
            areIn &= surface != GpuContract.NoTexture && normals != GpuContract.NoTexture;
            _cardTable.Set(entry.FirstCard + i, card.Row with { Surface = surface, Normals = normals });
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
            if (HasFailed(card.Card.Surface.Handle) || HasFailed(card.Card.Normals.Handle)) return true;
        }

        return false;
    }

    /// <summary>
    /// Reads or makes the model's field on a worker, from its coarsest clusters, for the render thread to take in
    /// (<see cref="TakeField"/>).
    /// </summary>
    private async Task MakeFieldAsync(ModelEntry entry, PackedModel packed)
    {
        ModelField? field = await Task.Run(() => ReadOrMakeFieldAsync(entry.Handle, packed.Coarsest), _stopping)
            .ConfigureAwait(false);
        _fieldsMade.Writer.TryWrite((entry, packed, field));
    }

    /// <summary>
    /// The model leaves: its clusters leave the pool and its row and field are freed.
    /// </summary>
    private void Evict(Handle<Model> model, ModelEntry entry)
    {
        _ready.Remove(entry);
        Free(entry);
        _modelTable.Set(entry.Row, default);
        _modelRows.Free(entry.Row, 1);
        FreeField(model);
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
            Release(card.Card.Surface.Handle);
            Release(card.Card.Normals.Handle);
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
        _geometry = _resources.MakeBuffer(GpuContract.Geometry, (uint)_wordCapacity * sizeof(uint));
        foreach (ModelEntry entry in _models.Entries.Values)
        {
            if (entry.In is { } packed)
            {
                _resources.Upload(
                _geometry,
                (uint)entry.FirstWord * sizeof(uint),
                MemoryMarshal.AsBytes(packed.Words.AsSpan()));
            }
        }
    }

    /// <summary>
    /// Takes in what workers finished for textures since the last frame: each texture prepared is placed in its pool,
    /// and each that did not load is counted failed.
    /// </summary>
    private void TakeTextures()
    {
        _textures.Take(TakeTexture);
    }

    /// <summary>
    /// The texture's levels at its pool's size, compressed, from the cache or made on a worker, for the render thread
    /// to place.
    /// </summary>
    private async Task<PreparedTexture> PrepareAsync(Texture texture)
    {
        int pool = GetPool(texture);
        byte[][] levels = await Task.Run(() => ReadOrCompressAsync(texture, pool), _stopping).ConfigureAwait(false);
        return new PreparedTexture(pool, levels);
    }

    /// <summary>
    /// What the cache keeps of the texture for its pool, or its levels resampled and compressed, then kept. A texture
    /// with no file to be versioned by is kept by its pixels.
    /// </summary>
    private async Task<byte[][]> ReadOrCompressAsync(Texture texture, int pool)
    {
        ulong version = _assets.GetVersion(texture.Handle);
        version = (version != 0 ? version : XxHash3.HashToUInt64(texture.Pixels.Span)) ^ (ulong)pool;
        if (await _caches.ReadAsync<CompressedTexture>(texture.Handle.Id, version, _stopping)
            .ConfigureAwait(false) is { Ok: true } cached)
        {
            return cached.Value.Levels;
        }

        uint size = (uint)_poolSizes[pool % 4];
        byte[][] levels = GpuConverter.Convert(texture, size, GetMipCount(size));
        await _caches.WriteAsync(texture.Handle.Id, version, new CompressedTexture(levels), _stopping)
            .ConfigureAwait(false);
        return levels;
    }

    /// <summary>
    /// A texture prepared for its pool, placed in it, or that did not load (none), counted failed.
    /// </summary>
    private void TakeTexture(Handle<Texture> texture, TextureEntry entry, PreparedTexture? prepared)
    {
        if (prepared is null)
        {
            entry.HasFailed = true;
            _changedTextures.Add(texture);
            return;
        }

        Place(texture, entry, prepared.Pool, prepared.Levels);
    }

    /// <summary>
    /// Gives the texture a layer of its pool, unless it has one there, and uploads its levels into it.
    /// </summary>
    private void Place(Handle<Texture> texture, TextureEntry entry, int pool, byte[][] levels)
    {
        if (entry.Ref == GpuContract.NoTexture || Unpack(entry.Ref).Pool != pool)
        {
            if (entry.Ref != GpuContract.NoTexture) FreeLayer(entry.Ref);

            entry.Ref = ((uint)pool << 16)
                | (uint)_pools[pool].Allocate(_resources, GpuContract.TexturePools[pool], _sampler);
        }

        entry.HasFailed = false;
        _changedTextures.Add(texture);
        Upload(texture, levels);
    }

    private void Upload(Handle<Texture> texture, byte[][] levels)
    {
        if (!_textures.Entries.TryGetValue(texture, out TextureEntry? entry)
            || entry.Ref == GpuContract.NoTexture)
        {
            return;
        }

        (int index, uint layer) = Unpack(entry.Ref);
        TexturePool pool = _pools[index];
        if (pool.HasGrown && layer < pool.Copied)
        {
            _heldBack.Add((texture, levels));
            return;
        }

        for (int level = 0; level < levels.Length; level++)
        {
            _resources.Upload(new GpuTextureRegion(pool.Texture, (uint)level, layer), levels[level]);
        }
    }

    /// <summary>
    /// The texture leaves: its layer goes back to its pool, for the next texture to take.
    /// </summary>
    private void Evict(Handle<Texture> texture, TextureEntry entry)
    {
        _changedTextures.Remove(texture);
        if (entry.Ref != GpuContract.NoTexture) FreeLayer(entry.Ref);
    }

    /// <summary>
    /// A texture's layer goes back to its pool, for the next texture to take.
    /// </summary>
    private void FreeLayer(uint textureRef)
    {
        (int pool, uint layer) = Unpack(textureRef);
        _pools[pool].Free(layer);
    }

    /// <summary>
    /// Takes in the materials loaded and the surfaces read since the last frame, packs again the materials whose
    /// textures came in or failed or whose surface failed to compile, and draws those whose pipelines are compiled now.
    /// </summary>
    private void TakeMaterials()
    {
        while (_surfacesRead.Reader.TryRead(out (Handle<Shader> Surface, SurfaceSource? Source) read))
        {
            TakeSurface(read.Surface, read.Source);
        }

        _materials.Take(TakeMaterial);

        if (_failures.TakeBroke(_broke) || _changedTextures.Count > 0)
        {
            foreach ((Handle<Material> handle, MaterialEntry entry) in _materials.Entries)
            {
                if (entry.Asset is { } material
                    && (IsUsingAnyOf(material, _changedTextures) || _broke.Contains(material.Shader)))
                {
                    Pack(handle, entry);
                }
            }

            _broke.Clear();
        }

        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            if (!_materials.Entries.TryGetValue(_waiting[i], out MaterialEntry? entry)
                || entry.Waiting is not { } waiting)
            {
                _waiting.RemoveAt(i);
                continue;
            }

            if (!IsDrawable(waiting.Drawn)) continue;

            Show(_waiting[i], entry, waiting);
            _waiting.RemoveAt(i);
        }
    }

    /// <summary>
    /// Takes in a material, or that it failed: the textures it samples held in place of those it sampled before, its
    /// surface read first if it is new, then the material packed.
    /// </summary>
    private void TakeMaterial(Handle<Material> handle, MaterialEntry entry, Material? material)
    {
        HoldTextures(material);
        HoldTextures(entry.Asset, release: true);
        entry.Asset = material;
        if (material is not null && !_surfaces.ContainsKey(material.Shader))
        {
            _surfaces[material.Shader] = new SurfaceEntry();
            ReadSurfaceAsync(material.Shader).FireAndForget();
            return;
        }

        Pack(handle, entry);
    }

    /// <summary>
    /// Holds every texture <paramref name="material"/> samples, or lets each go.
    /// </summary>
    private void HoldTextures(Material? material, bool release = false)
    {
        if (material is null) return;

        foreach (MaterialParam param in material.Params.Values)
        {
            if (release)
            {
                Release(param.Texture);
            }
            else
            {
                Record(param.Texture);
            }
        }
    }

    private async Task ReadSurfaceAsync(Handle<Shader> surface)
    {
        Result<SurfaceSource> source = await SurfaceSource.ReadAsync(_assets, surface, _stopping).ConfigureAwait(false);
        if (!source.Ok)
        {
            FailureHandler.Report(
            "A surface cannot be drawn, so its materials are drawn as the failure",
            source.Error);
        }

        _surfacesRead.Writer.TryWrite((surface, source.Ok ? source.Value : null));
    }

    private void TakeSurface(Handle<Shader> surface, SurfaceSource? source)
    {
        if (!_surfaces.TryGetValue(surface, out SurfaceEntry? entry)) return;

        entry.Source = source;
        entry.IsRead = true;
        foreach ((Handle<Material> handle, MaterialEntry material) in _materials.Entries)
        {
            if (material.Asset is { } asset && asset.Shader == surface) Pack(handle, material);
        }
    }

    /// <summary>
    /// Packs a material: its surface's packing of its params, or as the failure material for what broke when it, its
    /// surface or a texture it samples is; drawn once its pipelines are compiled. Nothing while a texture it samples is
    /// loading: it is packed again once the texture is in.
    /// </summary>
    private void Pack(Handle<Material> handle, MaterialEntry entry)
    {
        if (entry.Asset is not { } material)
        {
            PackAsFailure(handle, entry, FailureType.Shader, "Material does not load", doubleSided: false);
            return;
        }

        if (!_surfaces.TryGetValue(material.Shader, out SurfaceEntry? surface) || !surface.IsRead) return;

        if (surface.Source is not { } source || _failures.IsBroken(material.Shader))
        {
            PackAsFailure(handle, entry, FailureType.Shader, "Surface does not compile", material.DoubleSided);
            return;
        }

        (bool hasFailedTexture, bool isLoading) = CheckTextures(material);
        if (hasFailedTexture)
        {
            PackAsFailure(handle, entry, FailureType.Texture, "Texture does not load", material.DoubleSided);
            return;
        }

        if (isLoading) return;

        foreach (string unknown in source.Layout.FindUnknown(material.Params.Keys))
        {
            Debugging.Warn(
                $"{_assets.GetPath(handle)} sets {unknown}, which {source.Name}'s MaterialParams does not declare.");
        }

        GpuMaterial row = default;
        IReadOnlyDictionary<string, MaterialParam>?[] layers = [material.Params];
        source.Layout.Write(
            layers,
            param => GetRef(param.Texture),
            _ => null,
            MemoryMarshal.AsBytes(new Span<GpuMaterial>(ref row)));
        (Vector3 color, Vector3 emission) = source.Layout.GiOf(material.Params);
        GpuMaterialGi gi = new() { Color = new Vector4(color, 1), Emission = new Vector4(emission, 0) };
        DrawnMaterial drawn = new(
            entry.Row,
            new SurfaceKey(material.Shader, material.Type == MaterialType.Masked, material.DoubleSided),
            material.Type,
            source.IsSky);
        entry.Broken = null;
        ShowWhenDrawable(handle, entry, new PackedMaterial(row, gi, drawn, null));
    }

    /// <summary>
    /// Whether a texture <paramref name="material"/> samples failed, and whether one is still loading.
    /// </summary>
    private (bool HasFailed, bool IsLoading) CheckTextures(Material material)
    {
        bool isLoading = false;
        foreach (MaterialParam param in material.Params.Values)
        {
            if (param.Texture == Handle<Texture>.None) continue;

            if (HasFailed(param.Texture)) return (true, false);

            isLoading |= GetRef(param.Texture) == GpuContract.NoTexture;
        }

        return (false, isLoading);
    }

    /// <summary>
    /// Packs the material as the failure material for <paramref name="type"/>, in its own row, opaque, keeping its
    /// sides, once that material is drawn: until then it stays as it was, and is packed again when it is
    /// (<see cref="Show"/>). A failure material that is itself broken stands in for nothing.
    /// </summary>
    private void PackAsFailure(
        Handle<Material> handle,
        MaterialEntry entry,
        FailureType type,
        string why,
        bool doubleSided)
    {
        entry.Broken = type;
        Handle<Material> failure = _failures.GetMaterial(type);
        if (handle == failure || !_materials.Entries.TryGetValue(failure, out MaterialEntry? failed)
            || failed.Packed is not { } packed)
        {
            return;
        }

        DrawnMaterial drawn = new(
            entry.Row,
            packed.Drawn.Surface with { DoubleSided = doubleSided },
            MaterialType.Opaque,
            IsSky: false);
        ShowWhenDrawable(handle, entry, packed with { Gi = FailureHandler.Gi, Drawn = drawn, Failure = why });
    }

    /// <summary>
    /// Whether a material packed can be drawn as it is packed: a surface once every pipeline it is drawn with is
    /// compiled, a sky at once, as the sky draw compiles its own and draws nothing until then.
    /// </summary>
    private bool IsDrawable(DrawnMaterial drawn) => drawn.IsSky || _graph.IsDrawable(drawn.Surface, drawn.Type);

    /// <summary>
    /// Draws a material packed as soon as its pipelines are compiled: now, or once they are
    /// (<see cref="TakeMaterials"/>), drawn as it was until then.
    /// </summary>
    private void ShowWhenDrawable(Handle<Material> handle, MaterialEntry entry, PackedMaterial packed)
    {
        if (IsDrawable(packed.Drawn))
        {
            Show(handle, entry, packed);
            return;
        }

        if (entry.Waiting is null) _waiting.Add(handle);

        entry.Waiting = packed;
    }

    /// <summary>
    /// Writes a material's row as packed, what global illumination takes of it and the bins it is drawn by; a failure
    /// material drawn packs again the broken materials waiting for it.
    /// </summary>
    private void Show(Handle<Material> handle, MaterialEntry entry, PackedMaterial packed)
    {
        entry.Waiting = null;
        entry.Packed = packed;
        _materialTable.Set(entry.Row, packed.Record);
        _materialGiTable.Set(entry.Row, packed.Gi);
        WriteBins(packed.Drawn);
        ChangedMaterials.Add(handle);
        if (!_failures.IsMaterial(handle)) return;

        foreach ((Handle<Material> broken, MaterialEntry waiting) in _materials.Entries)
        {
            if (broken != handle && waiting.Broken is { } type
                && _failures.GetMaterial(type) == handle)
            {
                Pack(broken, waiting);
            }
        }
    }

    /// <summary>
    /// The row's bins word: the raster bin of its surface, the resolve bin of its surface and sides, made when they are
    /// first needed, and how it is drawn. A transparent material has neither; it is drawn forward. A sky has neither
    /// either, as no mesh draws it (<see cref="IsSky"/>).
    /// </summary>
    private void WriteBins(DrawnMaterial drawn)
    {
        if (drawn.IsSky)
        {
            _binTable.Set(drawn.Row, BinReady);
            return;
        }

        SurfaceKey surface = drawn.Surface;
        uint flags = BinReady | (surface.Masked ? BinMasked : 0) | (surface.DoubleSided ? BinDoubleSided : 0);
        if (drawn.Type == MaterialType.Transparent)
        {
            TransparentSurfaces.Add(surface);
            _binTable.Set(drawn.Row, flags | BinTransparent);
            return;
        }

        uint raster = (uint)(GpuContract.FixedRasterBins
            + BinOf(_rasterBins, RasterBins, surface, GpuContract.CardBins - GpuContract.FixedRasterBins));
        uint resolve = (uint)BinOf(_resolveBins, ResolveBins, surface with { Masked = false }, MaxBins);
        _binTable.Set(drawn.Row, flags | raster | (resolve << 16));

        // A surface past the bins there are is drawn with the last one's, and said: never left out.
        static int BinOf(Dictionary<SurfaceKey, int> bins, List<SurfaceKey> order, SurfaceKey key, int most)
        {
            if (bins.TryGetValue(key, out int bin)) return bin;

            if (order.Count >= most)
            {
                Debugging.Warn(
                    $"A frame has more than {most} kinds of surface, so the rest are drawn with the last kind.");
                return order.Count - 1;
            }

            bins[key] = order.Count;
            order.Add(key);
            return order.Count - 1;
        }
    }

    /// <summary>
    /// The material leaves: its row is freed, and the textures it samples let go.
    /// </summary>
    private void Evict(Handle<Material> material, MaterialEntry entry)
    {
        _materialRows.Free(entry.Row, 1);
        _binTable.Set(entry.Row, 0);
        ChangedMaterials.Remove(material);
        HoldTextures(entry.Asset, release: true);
    }

    /// <summary>
    /// The model's field from <paramref name="meshes"/>, from the cache, or made and kept there; none for no triangles.
    /// It is kept by the model's version, or for a model a gem made, which has no file, by a hash of the triangles it
    /// is made from. On a worker.
    /// </summary>
    private async Task<ModelField?> ReadOrMakeFieldAsync(Handle<Model> model, IReadOnlyList<Mesh> meshes)
    {
        ulong version = _assets.GetVersion(model);
        version = version != 0 ? version : HashOf(meshes);
        if (await _caches.ReadAsync<ModelField>(model.Id, version, _stopping)
            .ConfigureAwait(false) is { Ok: true } cached)
        {
            return cached.Value;
        }

        ModelField? made = GpuConverter.Convert(meshes);
        if (made is not null) await _caches.WriteAsync(model.Id, version, made, _stopping).ConfigureAwait(false);

        return made;
    }

    /// <summary>
    /// Packs <paramref name="field"/> as <paramref name="model"/>'s, in place of the one it had: the slot its row
    /// names, 0 for none when it has none or the atlas is full.
    /// </summary>
    private uint PlaceField(Handle<Model> model, ModelField? field)
    {
        FreeField(model);
        if (field is null || !_freeFieldSlots.TryPop(out int slot)) return 0;

        _fieldSlots[model] = slot;
        uint x = (uint)(slot % FieldsAcross * FieldResolution);
        uint y = (uint)(slot / FieldsAcross % FieldsAcross * FieldResolution);
        uint z = (uint)(slot / (FieldsAcross * FieldsAcross) * FieldResolution);
        _resources.Upload(
            new GpuTextureRegion(_fieldAtlas, 0, 0, x, y, z, FieldResolution, FieldResolution, FieldResolution),
            MemoryMarshal.AsBytes(field.Distances.AsSpan()));
        _fieldTable.Set(slot, new GpuMeshField { Min = field.Min, Size = field.Size, Corner = new Vector3(x, y, z) });
        return (uint)slot;
    }

    private void FreeField(Handle<Model> model)
    {
        if (!_fieldSlots.Remove(model, out int slot)) return;

        _fieldTable.Set(slot, default);
        _freeFieldSlots.Push(slot);
    }

    /// <summary>
    /// A camera's render texture loaded: its texture made at its size, in place of any it had. One that did not load
    /// draws nothing.
    /// </summary>
    private void TakeRenderTexture(Handle<RenderTexture> handle, RenderTextureEntry entry, RenderTexture? loaded)
    {
        if (loaded is null) return;

        uint width = (uint)Math.Max(1, loaded.Width);
        uint height = (uint)Math.Max(1, loaded.Height);
        if (entry.Made.Texture != GpuTexture.None) _resources.Release(entry.Made.Texture);

        GpuTexture texture = _resources.MakeTexture(new GpuTextureDescription(
            GpuFormat.Rgba16Float,
            GpuTextureUsage.Sampled | GpuTextureUsage.ColorTarget,
            width,
            height));
        entry.Made = BoundResource.Of(texture, width, height);
    }

    /// <summary>
    /// The render texture leaves, and its texture is released.
    /// </summary>
    private void Evict(Handle<RenderTexture> handle, RenderTextureEntry entry)
    {
        if (entry.Made.Texture != GpuTexture.None) _resources.Release(entry.Made.Texture);
    }

    /// <summary>
    /// The assets of one kind held resident, and the flow every kind shares: how many hold each, one count however many
    /// reasons there are; its entry, made the first time it is held, which starts its load; the load on a worker, where
    /// the asset is made ready for the GPU (<c>prepare</c>), or said and handed over as none when it does not load;
    /// taking in on the render thread what arrived for entries still held (<see cref="Take"/>); and letting go of those
    /// nothing holds any more (<see cref="Purge"/>). What each kind does with what arrived, and what leaving frees, is
    /// its own.
    /// </summary>
    private sealed class Resident<T, TEntry, TReady>(
        Assets assets,
        string failure,
        Func<T, Task<TReady>> prepare,
        CancellationToken stopping)
        where T : IAsset<T>
        where TEntry : class
        where TReady : class
    {
        private readonly Dictionary<Handle<T>, int> _holds = [];

        /// <summary>
        /// The assets let go to none since the last purge, which it lets leave unless they were held again.
        /// </summary>
        private readonly HashSet<Handle<T>> _unheld = [];

        /// <summary>
        /// What workers made ready, or none for an asset that did not load, for the render thread to take in.
        /// </summary>
        private readonly Channel<(Handle<T> Handle, TEntry Entry, TReady? Ready)> _arrived =
            Channel.CreateUnbounded<(Handle<T> Handle, TEntry Entry, TReady? Ready)>(
                new UnboundedChannelOptions { SingleReader = true });

        /// <summary>
        /// The entry of each asset held, or let go and not yet purged.
        /// </summary>
        internal Dictionary<Handle<T>, TEntry> Entries { get; } = [];

        /// <summary>
        /// Holds <paramref name="handle"/>: its entry, made by <paramref name="make"/> the first time, which starts its
        /// load.
        /// </summary>
        internal TEntry Record(Handle<T> handle, Func<TEntry> make)
        {
            _holds[handle] = _holds.GetValueOrDefault(handle) + 1;
            if (Entries.TryGetValue(handle, out TEntry? entry)) return entry;

            entry = make();
            Entries[handle] = entry;
            LoadAsync(handle, entry).FireAndForget();
            return entry;
        }

        /// <summary>
        /// Lets one hold go: once none holds it, the next <see cref="Purge"/> lets it leave.
        /// </summary>
        internal void Release(Handle<T> handle)
        {
            if (!_holds.TryGetValue(handle, out int count)) return;

            if (count > 1)
            {
                _holds[handle] = count - 1;
                return;
            }

            _holds.Remove(handle);
            _unheld.Add(handle);
        }

        /// <summary>
        /// Whether <paramref name="entry"/> is still the asset's: what a worker made for one let go meanwhile is
        /// dropped.
        /// </summary>
        internal bool IsCurrent(Handle<T> handle, TEntry entry)
            => Entries.TryGetValue(handle, out TEntry? now) && ReferenceEquals(now, entry);

        /// <summary>
        /// The asset's file changed and it loaded again: it is made ready again for the entry it has, when it is held.
        /// </summary>
        internal void Reload(T asset)
        {
            if (!Entries.TryGetValue(asset.Handle, out TEntry? entry)) return;

            PrepareAsync(asset.Handle, entry, asset)
            .FireAndForget();
        }

        /// <summary>
        /// Hands <paramref name="take"/> what arrived since the last frame for each entry still held: what was made
        /// ready, or none when it did not load.
        /// </summary>
        internal void Take(Action<Handle<T>, TEntry, TReady?> take)
        {
            while (_arrived.Reader.TryRead(out (Handle<T> Handle, TEntry Entry, TReady? Ready) arrived))
            {
                if (IsCurrent(arrived.Handle, arrived.Entry)) take(arrived.Handle, arrived.Entry, arrived.Ready);
            }
        }

        /// <summary>
        /// Lets every asset nothing holds any more leave, its entry handed to <paramref name="evict"/>.
        /// </summary>
        internal void Purge(Action<Handle<T>, TEntry> evict)
        {
            foreach (Handle<T> handle in _unheld)
            {
                if (!_holds.ContainsKey(handle) && Entries.Remove(handle, out TEntry? entry)) evict(handle, entry);
            }

            _unheld.Clear();
        }

        private async Task LoadAsync(Handle<T> handle, TEntry entry)
        {
            Result<T> loaded = await assets.LoadAsync(handle, stopping).ConfigureAwait(false);
            if (loaded.Ok)
            {
                await PrepareAsync(handle, entry, loaded.Value).ConfigureAwait(false);
                return;
            }

            FailureHandler.Report(failure, loaded.Error);
            _arrived.Writer.TryWrite((handle, entry, null));
        }

        private async Task PrepareAsync(Handle<T> handle, TEntry entry, T asset)
        {
            TReady ready = await prepare(asset).ConfigureAwait(false);
            _arrived.Writer.TryWrite((handle, entry, ready));
        }
    }

    /// <summary>
    /// A model as the pool holds it: its row, what was packed and is in, what is packed and waits its turn, where it is
    /// in the pool, its field's row, whether its cards' atlases are all in, and how near the player the nearest
    /// instance asking for it was.
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

        internal uint Field { get; set; }

        internal bool AreCardsIn { get; set; }

        internal float Distance { get; set; } = float.MaxValue;

        internal bool HasFailed { get; set; }
    }

    /// <summary>
    /// A texture as the pools hold it: its reference, <see cref="GpuContract.NoTexture"/> until it is in.
    /// </summary>
    private sealed class TextureEntry
    {
        internal uint Ref { get; set; } = GpuContract.NoTexture;

        internal bool HasFailed { get; set; }
    }

    /// <summary>
    /// A texture made ready for its pool on a worker: its levels compressed for the pool at index <see cref="Pool"/>.
    /// </summary>
    private sealed record PreparedTexture(int Pool, byte[][] Levels);

    /// <summary>
    /// A camera's render texture as it is held: its texture once it is made.
    /// </summary>
    private sealed class RenderTextureEntry
    {
        internal BoundResource Made { get; set; }
    }

    /// <summary>
    /// One texture array: a format and a square size, its layers handed out in order, those let go first, doubled when
    /// they run out.
    /// </summary>
    private sealed class TexturePool(uint size)
    {
        /// <summary>
        /// Layers let go, which the next textures take before any new one.
        /// </summary>
        private readonly Stack<uint> _free = new();

        private uint _capacity;
        private uint _used;

        internal GpuTexture Texture { get; private set; }

        /// <summary>
        /// It grew this frame, so its first <see cref="Copied"/> layers are written by the copy from the old array.
        /// </summary>
        internal bool HasGrown { get; set; }

        internal uint Copied { get; private set; }

        internal int Allocate(GpuResources resources, ResourceInfo info, GpuSampler sampler)
        {
            if (_free.TryPop(out uint layer)) return (int)layer;

            if (_used == _capacity) Grow(resources, info, sampler, Math.Max(4, _capacity * 2));

            return (int)_used++;
        }

        internal void Free(uint layer)
        {
            _free.Push(layer);
        }

        /// <summary>
        /// Doubles the pool's layers, under its entry in place of the array it had, which the manager releases once the
        /// copy from it is recorded.
        /// </summary>
        private void Grow(GpuResources resources, ResourceInfo info, GpuSampler sampler, uint capacity)
        {
            uint levels = (uint)GetMipCount(size);
            GpuTexture grown = resources.MakeTexture(info, size, size, levels, capacity, sampler);
            for (uint layer = 0; layer < _used; layer++)
            {
                for (uint level = 0; level < levels; level++)
                {
                    resources.Copy(
                        new GpuTextureRegion(Texture, level, layer),
                        new GpuTextureRegion(grown, level, layer));
                }
            }

            HasGrown = _used > 0;
            Copied = _used;
            Texture = grown;
            _capacity = capacity;
        }
    }

    private sealed class MaterialEntry(int row)
    {
        internal int Row { get; } = row;

        internal Material? Asset { get; set; }

        /// <summary>
        /// What it is drawn as; null until it is drawn.
        /// </summary>
        internal PackedMaterial? Packed { get; set; }

        internal DrawnMaterial? Drawn => Packed?.Drawn;

        /// <summary>
        /// What it was packed as last, while it waits for its pipelines to be drawn so; null when it does not wait.
        /// </summary>
        internal PackedMaterial? Waiting { get; set; }

        /// <summary>
        /// What broke, when it is packed as a failure material or waits for one to be drawn; null when it is not
        /// broken.
        /// </summary>
        internal FailureType? Broken { get; set; }

        /// <summary>
        /// Why it is drawn as the failure, in a few words; null when it is not.
        /// </summary>
        internal string? Failure => Packed?.Failure;
    }

    /// <summary>
    /// A material packed: its row, what global illumination takes of it, what it is drawn with, and why it is drawn as
    /// the failure, null when it is not.
    /// </summary>
    private sealed record PackedMaterial(GpuMaterial Record, GpuMaterialGi Gi, DrawnMaterial Drawn, string? Failure);

    private sealed class SurfaceEntry
    {
        internal SurfaceSource? Source { get; set; }

        internal bool IsRead { get; set; }
    }
}

/// <summary>
/// A model packed for the <see cref="GpuCache"/>: its words, its clusters with their words counted from its first, its
/// levels with their clusters counted from its first, its cards, its sphere, how far from the sphere's middle its
/// clusters' error spheres reach, its grid's step, the unit-scale distance its cards take over from its clusters (0 for
/// none), and its coarsest triangles.
/// </summary>
internal sealed record PackedModel(
    uint[] Words,
    GpuCluster[] Clusters,
    GpuLevel[] Levels,
    PackedCard[] Cards,
    Vector4 Bounds,
    float ClusterReach,
    float Step,
    float CardDistance,
    IReadOnlyList<Mesh> Coarsest);

/// <summary>
/// A texture as its pool holds it and the cache keeps it: each level's BC7 blocks.
/// </summary>
internal sealed record CompressedTexture(byte[][] Levels)
{
    /// <summary>
    /// Raised whenever how a texture is resampled or compressed changes, so the cache's are made again.
    /// </summary>
    internal const int CacheVersion = 2;

    internal static Result<CompressedTexture> Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        byte[][] levels = new byte[reader.ReadInt32()][];
        for (int level = 0; level < levels.Length; level++)
        {
            levels[level] = reader.ReadBytes(reader.ReadInt32());
        }

        return Result.Success(new CompressedTexture(levels));
    }

    /// <summary>
    /// The levels as the cache keeps them: how many, then each one's length and its bytes.
    /// </summary>
    internal void Write(Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(Levels.Length);
        foreach (byte[] level in Levels)
        {
            writer.Write(level.Length);
            writer.Write(level);
        }
    }
}

/// <summary>
/// A model's distance field: its box in the model's space, the corner of its first texel and the size from it to the
/// last, and <see cref="GpuCache.FieldResolution"/> cubed distances in that space, x fastest.
/// </summary>
internal sealed record ModelField(Vector3 Min, Vector3 Size, Half[] Distances)
{
    /// <summary>
    /// Raised whenever how a field is made or written changes, so the cache's older ones are made again.
    /// </summary>
    internal const int CacheVersion = 1;

    internal static Result<ModelField> Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        Vector3 min = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        Vector3 size = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        Half[] distances = new Half[GpuCache.FieldResolution * GpuCache.FieldResolution * GpuCache.FieldResolution];
        bytes.ReadExactly(MemoryMarshal.AsBytes(distances.AsSpan()));
        return Result.Success(new ModelField(min, size, distances));
    }

    /// <summary>
    /// The field as the cache keeps it: its box's corner and size, then its distances.
    /// </summary>
    internal void Write(Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(Min.X);
        writer.Write(Min.Y);
        writer.Write(Min.Z);
        writer.Write(Size.X);
        writer.Write(Size.Y);
        writer.Write(Size.Z);
        writer.Write(MemoryMarshal.AsBytes(Distances.AsSpan()));
    }
}
