using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// The one route to the GPU's memory: every buffer, texture, table and sampler the renderer uses is made, grown, sent
/// and released here, and found here by the name passes read it by. A part asks for what it needs under its contract
/// entry (<see cref="GpuContract"/>), which says its name, shape and use, and keeps what it is handed to write into;
/// whoever records finds it in a scope (<see cref="ResourceScope"/>): the frame's, a view's, or a target's, each name
/// as it stands now, a table's buffer however often it grew. Nothing else makes, uploads or releases GPU memory, so
/// what is on the GPU, and how much of it each kind takes, is known in one place. Render thread.
/// <para>
/// Assets come the same way: a part asks for a model, material or render texture by its handle and is handed its row or
/// texture, the asset loaded the first time and kept while anything holds it. Keeping what is held resident, loading it
/// and making it ready for the GPU happen behind this, in the cache and the converter, which nothing else names.
/// </para>
/// <para>
/// What is made again under an entry, a table or buffer outgrowing itself or a pool doubling, is bound at once; the one
/// it replaces is released at the next <see cref="Upload()"/>, after anything recorded from it, as a pool's growing
/// copies from it. Every upload reaches the GPU at the start of the next submit, before any command, in the order made.
/// </para>
/// </summary>
internal sealed class GpuResources : IDisposable
{
    /// <summary>
    /// Indices a piece is drawn with, up to 32 triangles; a tile's quad, or a card, takes the first six.
    /// </summary>
    internal const uint PieceIndexCount = 96;

    private readonly IGpu _gpu;
    private readonly List<ResourceScope> _scopes = [];
    private readonly Dictionary<GpuSamplerDescription, GpuSampler> _samplers = [];

    /// <summary>
    /// What was made again since the last <see cref="Upload()"/>, released then.
    /// </summary>
    private readonly List<BoundResource> _retired = [];

    /// <summary>
    /// What is made with no entry, as only the renderer's own code reads it, by what it is.
    /// </summary>
    private readonly HashSet<GpuTexture> _textures = [];
    private readonly HashSet<GpuBuffer> _buffers = [];

    /// <summary>
    /// What keeps the assets parts hold resident, through this.
    /// </summary>
    private readonly GpuCache _cache;

    private GpuTexture _window;

    internal GpuResources(
        IGpu gpu,
        Assets assets,
        Caches caches,
        PassGraph graph,
        FailureHandler failures,
        ClusterRenderingSettings settings,
        CancellationToken stopping)
    {
        _gpu = gpu;
        Frame = MakeScope();
        PieceIndices = MakeBuffer(GpuBufferUsage.Index, PieceIndexCount * sizeof(ushort));
        ushort[] indices = new ushort[PieceIndexCount];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (ushort)i;
        }

        Upload(PieceIndices, 0, MemoryMarshal.AsBytes(indices.AsSpan()));
        GpuTable<uint> zBinsEmpty = MakeTable<uint>(GpuContract.ZBinsEmpty, Views.ZBinCount * 2);
        for (int bin = 0; bin < Views.ZBinCount; bin++)
        {
            zBinsEmpty.Set(bin * 2, uint.MaxValue);
        }

        MakeTable<uint>(GpuContract.Zeros, 16384);
        _cache = new GpuCache(this, assets, caches, graph, failures, settings, stopping);
    }

    /// <summary>
    /// The frame's scope: the frame-wide tables and buffers, the texture pools, the shadows, and what passes make for
    /// the frame.
    /// </summary>
    internal ResourceScope Frame { get; }

    /// <summary>
    /// The indices 0 to <see cref="PieceIndexCount"/>, which a draw of pieces or tiles steps through an instance at a
    /// time.
    /// </summary>
    internal GpuBuffer PieceIndices { get; }

    /// <inheritdoc cref="GpuCache.ChangedModels"/>
    internal HashSet<Handle<Model>> ChangedModels => _cache.ChangedModels;

    /// <inheritdoc cref="GpuCache.ChangedMaterials"/>
    internal HashSet<Handle<Material>> ChangedMaterials => _cache.ChangedMaterials;

    /// <inheritdoc cref="GpuCache.RecolouredMaterials"/>
    internal HashSet<Handle<Material>> RecolouredMaterials => _cache.RecolouredMaterials;

    /// <inheritdoc cref="GpuCache.RasterBins"/>
    internal List<SurfaceKey> RasterBins => _cache.RasterBins;

    /// <inheritdoc cref="GpuCache.ResolveBins"/>
    internal List<SurfaceKey> ResolveBins => _cache.ResolveBins;

    /// <inheritdoc cref="GpuCache.TransparentSurfaces"/>
    internal HashSet<SurfaceKey> TransparentSurfaces => _cache.TransparentSurfaces;

    public void Dispose()
    {
        foreach (ResourceScope scope in _scopes)
        {
            scope.Release();
        }

        ReleaseRetired();
        foreach (GpuTexture texture in _textures)
        {
            _gpu.Release(texture);
        }

        foreach (GpuBuffer buffer in _buffers)
        {
            _gpu.Release(buffer);
        }

        foreach (GpuSampler sampler in _samplers.Values)
        {
            _gpu.Release(sampler);
        }

        if (_window != GpuTexture.None) _gpu.Release(_window);
    }

    /// <inheritdoc cref="GpuCache.Record(Handle{Model})"/>
    internal int Record(Handle<Model> model) => _cache.Record(model);

    /// <inheritdoc cref="GpuCache.Record(Handle{Material})"/>
    internal int Record(Handle<Material> material) => _cache.Record(material);

    /// <inheritdoc cref="GpuCache.Record(Handle{RenderTexture})"/>
    internal void Record(Handle<RenderTexture> texture) => _cache.Record(texture);

    /// <inheritdoc cref="GpuCache.Release(Handle{Model})"/>
    internal void Release(Handle<Model> model) => _cache.Release(model);

    internal void Release(Handle<Material> material) => _cache.Release(material);

    internal void Release(Handle<RenderTexture> texture) => _cache.Release(texture);

    /// <summary>
    /// Once a frame, after the parts let go of what they no longer draw: lets go of every asset nothing holds any more,
    /// then takes in what workers finished loading since the last frame.
    /// </summary>
    internal void Update()
    {
        _cache.Purge();
        _cache.Take();
    }

    /// <inheritdoc cref="GpuCache.EndFrame"/>
    internal void EndFrame() => _cache.EndFrame();

    /// <inheritdoc cref="GpuCache.Reload(Model)"/>
    internal void Reload(Model model) => _cache.Reload(model);

    /// <inheritdoc cref="GpuCache.Reload(Texture)"/>
    internal void Reload(Texture texture) => _cache.Reload(texture);

    /// <inheritdoc cref="GpuCache.Reload(Material)"/>
    internal void Reload(Material material) => _cache.Reload(material);

    /// <inheritdoc cref="GpuCache.Reload(Shader)"/>
    internal void Reload(Shader shader) => _cache.Reload(shader);

    internal bool HasFailed(Handle<Model> model) => _cache.HasFailed(model);

    /// <inheritdoc cref="GpuCache.IsDrawable(Handle{Model})"/>
    internal bool IsDrawable(Handle<Model> model) => _cache.IsDrawable(model);

    /// <inheritdoc cref="GpuCache.HasField"/>
    internal bool HasField(Handle<Model> model) => _cache.HasField(model);

    /// <inheritdoc cref="GpuCache.Prioritize"/>
    internal void Prioritize(Handle<Model> model, float distance) => _cache.Prioritize(model, distance);

    /// <inheritdoc cref="GpuCache.GetBounds"/>
    internal Vector4 GetBounds(Handle<Model> model) => _cache.GetBounds(model);

    /// <inheritdoc cref="GpuCache.FindInPool"/>
    internal (int FirstCluster, PackedModel Packed)? FindInPool(Handle<Model> model) => _cache.FindInPool(model);

    /// <inheritdoc cref="GpuCache.GetRow(Handle{Model})"/>
    internal int GetRow(Handle<Model> model) => _cache.GetRow(model);

    /// <inheritdoc cref="GpuCache.GetRow(Handle{Material})"/>
    internal int GetRow(Handle<Material> material) => _cache.GetRow(material);

    /// <inheritdoc cref="GpuCache.Get"/>
    internal DrawnMaterial? Get(Handle<Material> material) => _cache.Get(material);

    /// <inheritdoc cref="GpuCache.IsShown"/>
    internal bool IsShown(Handle<Material> material) => _cache.IsShown(material);

    /// <inheritdoc cref="GpuCache.IsSky"/>
    internal bool IsSky(Handle<Material> material) => _cache.IsSky(material);

    /// <inheritdoc cref="GpuCache.GetFailure"/>
    internal string? GetFailure(Handle<Material> material) => _cache.GetFailure(material);

    /// <inheritdoc cref="GpuCache.GetSky"/>
    internal (SurfaceKey Surface, int Row) GetSky(Handle<Material>? world) => _cache.GetSky(world);

    /// <inheritdoc cref="GpuCache.GetTexture"/>
    internal BoundResource GetTexture(Handle<RenderTexture> texture) => _cache.GetTexture(texture);

    /// <summary>
    /// A scope of its own, a view's or a target's, whose names are found before the frame's; it lives until
    /// <see cref="Release(ResourceScope)"/>.
    /// </summary>
    internal ResourceScope MakeScope()
    {
        ResourceScope scope = new(_gpu);
        _scopes.Add(scope);
        return scope;
    }

    /// <summary>
    /// Releases a scope and everything made in it.
    /// </summary>
    internal void Release(ResourceScope scope)
    {
        if (_scopes.Remove(scope)) scope.Release();
    }

    /// <summary>
    /// A table of <paramref name="rows"/> under <paramref name="info"/>, in the frame's scope or
    /// <paramref name="scope"/>: its owner sets rows, and <see cref="Upload()"/> sends what changed.
    /// </summary>
    internal GpuTable<T> MakeTable<T>(ResourceInfo info, int rows, ResourceScope? scope = null)
        where T : unmanaged
    {
        GpuTable<T> table = new(_gpu, info.BufferUsage, rows);
        (scope ?? Frame).Add(info, table);
        return table;
    }

    /// <summary>
    /// A table no pass reads by name, only the renderer's own code: a view's pieces drawn forward, the failure's words.
    /// </summary>
    internal GpuTable<T> MakeTable<T>(GpuBufferUsage usage, int rows, ResourceScope? scope = null)
        where T : unmanaged
    {
        GpuTable<T> table = new(_gpu, usage, rows);
        (scope ?? Frame).Add(null, table);
        return table;
    }

    /// <summary>
    /// A buffer of <paramref name="bytes"/> under <paramref name="info"/>, in place of the one it had, which is
    /// released at the next <see cref="Upload()"/>.
    /// </summary>
    internal GpuBuffer MakeBuffer(ResourceInfo info, uint bytes, ResourceScope? scope = null)
    {
        GpuBuffer buffer = _gpu.CreateBuffer(info.BufferUsage, bytes);
        Retire((scope ?? Frame).Own(info, BoundResource.Of(buffer, bytes)));
        return buffer;
    }

    /// <summary>
    /// A buffer no pass reads by name, until <see cref="Release(GpuBuffer)"/>.
    /// </summary>
    internal GpuBuffer MakeBuffer(GpuBufferUsage usage, uint bytes)
    {
        GpuBuffer buffer = _gpu.CreateBuffer(usage, bytes);
        _buffers.Add(buffer);
        return buffer;
    }

    /// <summary>
    /// A texture under <paramref name="info"/>, in its format, shape and use, <paramref name="depth"/> layers, or
    /// texels deep for a volume, read through <paramref name="sampler"/> wherever it is read when it has one; in place
    /// of the one it had, which is released at the next <see cref="Upload()"/>.
    /// </summary>
    internal GpuTexture MakeTexture(
        ResourceInfo info,
        uint width,
        uint height,
        uint levels = 1,
        uint depth = 1,
        GpuSampler sampler = default,
        ResourceScope? scope = null)
    {
        GpuTexture texture = _gpu.CreateTexture(
            new GpuTextureDescription(info.Format, info.TextureUsage, width, height, levels, depth, info.Shape));
        Retire((scope ?? Frame).Own(info, BoundResource.Of(texture, width, height, sampler)));
        return texture;
    }

    /// <summary>
    /// A texture no pass reads by name, until <see cref="Release(GpuTexture)"/>: a pool's empty stand-in, a camera's
    /// render texture.
    /// </summary>
    internal GpuTexture MakeTexture(in GpuTextureDescription description)
    {
        GpuTexture texture = _gpu.CreateTexture(description);
        _textures.Add(texture);
        return texture;
    }

    /// <summary>
    /// Releases what <paramref name="info"/> was made as in the frame's scope or <paramref name="scope"/>, at the next
    /// <see cref="Upload()"/>.
    /// </summary>
    internal void Release(ResourceInfo info, ResourceScope? scope = null)
    {
        Retire((scope ?? Frame).Disown(info));
    }

    internal void Release(GpuTexture texture)
    {
        if (_textures.Remove(texture)) _retired.Add(BoundResource.Of(texture, 0, 0));
    }

    internal void Release(GpuBuffer buffer)
    {
        if (_buffers.Remove(buffer)) _retired.Add(BoundResource.Of(buffer, 0));
    }

    /// <summary>
    /// The sampler a description makes, made the first time it is asked for and shared by everyone who asks.
    /// </summary>
    internal GpuSampler GetSampler(in GpuSamplerDescription description)
    {
        if (_samplers.TryGetValue(description, out GpuSampler sampler)) return sampler;

        sampler = _gpu.CreateSampler(description);
        _samplers[description] = sampler;
        return sampler;
    }

    /// <summary>
    /// The window's image, which views draw into their sections of; none without a window.
    /// </summary>
    internal GpuTexture GetWindowTarget(IWindow? window)
    {
        if (window is not null && _window == GpuTexture.None) _window = _gpu.GetWindowTarget(window);

        return _window;
    }

    internal void Upload(GpuBuffer buffer, uint offset, ReadOnlySpan<byte> bytes)
    {
        _gpu.Upload(buffer, offset, bytes);
    }

    internal void Upload(in GpuTextureRegion region, ReadOnlySpan<byte> bytes)
    {
        _gpu.Upload(region, bytes);
    }

    /// <summary>
    /// Copies texels on the GPU, in the order commands run, after every upload of the frame: what a pool's growing
    /// keeps.
    /// </summary>
    internal void Copy(in GpuTextureRegion from, in GpuTextureRegion to)
    {
        _gpu.Commands.CopyTexture(from, to);
    }

    /// <summary>
    /// Copies bytes on the GPU, in the order commands run: what a pass seeds what it makes with.
    /// </summary>
    internal void Copy(GpuBuffer from, uint fromOffset, GpuBuffer to, uint toOffset, uint bytes)
    {
        _gpu.Commands.CopyBuffer(from, fromOffset, to, toOffset, bytes);
    }

    /// <summary>
    /// Once a frame, after every row is set and before anything records: releases what was made again, and sends every
    /// table's rows set since the last upload.
    /// </summary>
    internal void Upload()
    {
        ReleaseRetired();
        foreach (ResourceScope scope in _scopes)
        {
            scope.UploadTables();
        }
    }

    private void Retire(BoundResource replaced)
    {
        if (replaced.Texture != GpuTexture.None || replaced.Buffer != GpuBuffer.None) _retired.Add(replaced);
    }

    private void ReleaseRetired()
    {
        foreach (BoundResource retired in _retired)
        {
            if (retired.Texture != GpuTexture.None) _gpu.Release(retired.Texture);

            if (retired.Buffer != GpuBuffer.None) _gpu.Release(retired.Buffer);
        }

        _retired.Clear();
    }
}

/// <summary>
/// The textures, buffers and tables names stand for in one scope, the frame, a view or a target: the renderer's, made
/// through <see cref="GpuResources"/> under their contract entries, each found as it stands now; and the passes', made
/// here as their keys say and made again when a key changes. Render thread.
/// </summary>
internal sealed class ResourceScope(IGpu gpu)
{
    /// <summary>
    /// What each name stands for: what was made under it, or the table it is, read as it stands.
    /// </summary>
    private readonly Dictionary<string, Named> _bound = new(StringComparer.Ordinal);

    /// <summary>
    /// What the renderer made here under its entries, released with the scope.
    /// </summary>
    private readonly Dictionary<string, BoundResource> _owned = new(StringComparer.Ordinal);
    private readonly List<IGpuTable> _tables = [];
    private readonly Dictionary<string, (BoundResource Resource, ResourceKey Key)> _made = new(StringComparer.Ordinal);

    /// <summary>
    /// What is kept from frame to frame, each with its history's name, so a frame's swap walks these and makes no name.
    /// </summary>
    private readonly List<(string Name, string History)> _histories = [];

    /// <summary>
    /// The listing last trimmed for, so a frame with the same one does nothing.
    /// </summary>
    private Listing? _kept;

    /// <summary>
    /// Releases everything made in the scope: its maker's, when the scope goes or the renderer stops.
    /// </summary>
    internal void Release()
    {
        foreach (string name in new List<string>(_made.Keys))
        {
            ReleaseMade(name);
        }

        foreach (BoundResource owned in _owned.Values)
        {
            gpu.Release(owned.Texture);
            gpu.Release(owned.Buffer);
        }

        foreach (IGpuTable table in _tables)
        {
            table.Release();
        }

        _owned.Clear();
        _tables.Clear();
        _bound.Clear();
    }

    internal bool TryGet(string name, out BoundResource resource)
    {
        if (_bound.TryGetValue(name, out Named? named))
        {
            resource = named.Table?.Bound ?? named.Resource;
            return true;
        }

        resource = default;
        return false;
    }

    internal bool TryGet(ResourceInfo info, out BoundResource resource) => TryGet(info.Name, out resource);

    /// <summary>
    /// Binds <paramref name="info"/> to what is made elsewhere and only drawn into here: a target's image.
    /// </summary>
    internal void Set(ResourceInfo info, BoundResource resource)
    {
        _bound[info.Name] = new Named(resource, null);
    }

    /// <summary>
    /// What a pass makes, made now unless it was made with the same key; returns whether it was made now, so whoever
    /// fills it knows. One kept from frame to frame is made with its history,
    /// <see cref="ResourceInfo.GetHistoryName"/>.
    /// </summary>
    internal bool MakeIfChanged(string name, in ResourceKey key)
    {
        if (_made.TryGetValue(name, out (BoundResource Resource, ResourceKey Key) made)
            && made.Key == key)
        {
            return false;
        }

        ReleaseMade(name);
        Make(name, key);
        if (!key.HasHistory) return true;

        string history = ResourceInfo.GetHistoryName(name);
        Make(history, key with { HasHistory = false });
        _histories.Add((name, history));

        return true;
    }

    /// <summary>
    /// Swaps each texture kept from frame to frame with its history, so what was written this frame is read as it the
    /// next, and the next frame writes over the older one.
    /// </summary>
    internal void Swap()
    {
        foreach ((string name, string history) in _histories)
        {
            (BoundResource Resource, ResourceKey Key) current = _made[name];
            (BoundResource Resource, ResourceKey Key) last = _made[history];
            _made[name] = (last.Resource, current.Key);
            _made[history] = (current.Resource, last.Key);
            _bound[name] = new Named(last.Resource, null);
            _bound[history] = new Named(current.Resource, null);
        }
    }

    /// <summary>
    /// Releases what the passes made that <paramref name="listing"/> does not make; what the renderer made here of its
    /// own names stays.
    /// </summary>
    internal void ReleaseUnlisted(Listing listing)
    {
        if (listing == _kept) return;

        _kept = listing;
        foreach (string name in new List<string>(_made.Keys))
        {
            if (_made.ContainsKey(name) && !listing.Created.ContainsKey(name) && !GpuContract.All.ContainsKey(name)
                && !IsHistoryOf(listing, name))
            {
                ReleaseMade(name);
            }
        }
    }

    /// <summary>
    /// Keeps a table made for this scope, found under <paramref name="info"/> when it has one.
    /// </summary>
    internal void Add(ResourceInfo? info, IGpuTable table)
    {
        _tables.Add(table);
        if (info is not null) _bound[info.Name] = new Named(default, table);
    }

    /// <summary>
    /// Keeps what the renderer made under <paramref name="info"/>, found under it from now on; what it had is handed
    /// back for its maker to release.
    /// </summary>
    internal BoundResource Own(ResourceInfo info, BoundResource made)
    {
        BoundResource replaced = _owned.GetValueOrDefault(info.Name);
        _owned[info.Name] = made;
        _bound[info.Name] = new Named(made, null);
        return replaced;
    }

    /// <summary>
    /// Forgets what the renderer made under <paramref name="info"/>, handed back for its maker to release.
    /// </summary>
    internal BoundResource Disown(ResourceInfo info)
    {
        _bound.Remove(info.Name);
        return _owned.Remove(info.Name, out BoundResource owned) ? owned : default;
    }

    internal void UploadTables()
    {
        foreach (IGpuTable table in _tables)
        {
            table.Upload();
        }
    }

    private static bool IsHistoryOf(Listing listing, string name)
    {
        return name.EndsWith(ResourceInfo.HistorySuffix, StringComparison.Ordinal)
            && listing.Created.TryGetValue(name[..^ResourceInfo.HistorySuffix.Length], out CreatedResource? created)
            && created.Create.History;
    }

    private void Make(string name, in ResourceKey key)
    {
        BoundResource resource = key.IsBuffer
            ? BoundResource.Of(gpu.CreateBuffer(key.BufferUsage, key.Bytes), key.Bytes)
            : BoundResource.Of(
                gpu.CreateTexture(new GpuTextureDescription(
                    key.Format,
                    key.TextureUsage,
                    key.Width,
                    key.Height,
                    key.Levels,
                    key.Layers,
                    key.Shape)),
                key.Width,
                key.Height);
        _made[name] = (resource, key);
        _bound[name] = new Named(resource, null);
    }

    /// <summary>
    /// Releases what <paramref name="name"/> was made as for a pass, and its history with it.
    /// </summary>
    private void ReleaseMade(string name)
    {
        if (!_made.Remove(name, out (BoundResource Resource, ResourceKey Key) made)) return;

        _bound.Remove(name);
        gpu.Release(made.Resource.Texture);
        gpu.Release(made.Resource.Buffer);
        if (!made.Key.HasHistory) return;

        _histories.RemoveAll(kept => string.Equals(kept.Name, name, StringComparison.Ordinal));
        ReleaseMade(ResourceInfo.GetHistoryName(name));
    }

    /// <summary>
    /// What a name stands for: what was made under it, or a table, read as it stands, as its buffer changes when it
    /// grows.
    /// </summary>
    private sealed record Named(BoundResource Resource, IGpuTable? Table);
}
