using System.Numerics;
using System.Runtime.CompilerServices;
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
/// Every material drawn, as a row of the materials buffer its surface's generated <c>LoadMaterialParams</c> reads, made
/// the first time it is asked for so what names it never changes as it loads, and every surface shader's params, read
/// once to pack its materials by. Each row also says how it is drawn (<see cref="Bins"/>): the raster bin its clusters
/// are drawn into the visibility buffer by when they clip (a masked surface's, or a card's or a fade's of any), the
/// resolve bin its pixels are shaded into the G-buffer by, and whether it is in yet, transparent, masked or
/// double-sided. A material, its surface and its textures load on workers and are taken in on the render thread; what is
/// broken is packed, in its own row, as the failure material for what broke (<see cref="Assets.Failure{T}"/>), a
/// material like any other, loaded as the renderer starts and kept. Render thread.
/// <para>
/// A material is drawn only once it can be drawn as it should be: its textures in, and every pipeline it is drawn with
/// compiled (<see cref="PassGraph.IsDrawable"/>). Until then its row stays as it was, not in for a new one, so nothing is
/// drawn flat, black or not at all for a while and then changes; a material packed again, its file or a texture of it
/// changed, keeps being drawn as it was until it can be drawn the new way.
/// </para>
/// </summary>
internal sealed class Materials : IDisposable
{
    /// <summary>
    /// The raster bins every frame has, whose pixels do not clip: single-sided and double-sided. A surface's own clusters'
    /// come after, and its cards' <see cref="CardBins"/> after those (Shaders/Include/Culling.hlsli's CardBinOffset).
    /// </summary>
    public const int FixedRasterBins = 2;

    /// <summary>
    /// The first surface's cards' raster bin; a surface's own clusters' bins are those before it.
    /// </summary>
    public const int CardBins = 128;

    /// <summary>
    /// Raster bins there are rows for, a phase's in the bins' draw arguments.
    /// </summary>
    public const int RasterBinRows = 256;

    /// <summary>
    /// A row's <see cref="Bins"/> word: its raster bin in the low byte, its resolve bin in the third, and these.
    /// </summary>
    public const uint BinMasked = 1u << 8;
    public const uint BinDoubleSided = 1u << 9;
    public const uint BinTransparent = 1u << 10;
    public const uint BinReady = 1u << 11;

    /// <summary>
    /// The most resolve bins there are, as a byte numbers them.
    /// </summary>
    private const int MaxBins = 256;

    /// <summary>
    /// What a broken material gives global illumination: mid grey, as if it were drawn.
    /// </summary>
    private static readonly GpuMaterialGi _failureGi = new() { Color = new Vector4(0.5f, 0.5f, 0.5f, 1) };

    private readonly Assets _assets;
    private readonly TexturePools _textures;
    private readonly PassGraph _graph;
    private readonly Threads _threads;
    private readonly CancellationToken _stopping;
    private readonly Dictionary<Handle<Material>, MaterialEntry> _materials = [];
    private readonly Dictionary<Handle<Shader>, SurfaceEntry> _surfaces = [];

    /// <summary>
    /// The materials packed and waiting for their pipelines to be drawn (<see cref="MaterialEntry.Waiting"/>), or let go
    /// since: those are passed over.
    /// </summary>
    private readonly List<Handle<Material>> _waiting = [];

    private readonly Dictionary<SurfaceKey, int> _rasterBins = [];
    private readonly Dictionary<SurfaceKey, int> _resolveBins = [];
    private readonly RangeAllocator _rows = new();
    private readonly Handle<Material> _default;

    /// <summary>
    /// The failure materials, by what broke, loading from the start; none for a kind whose file is missing, whose broken
    /// materials are then drawn as the default.
    /// </summary>
    private readonly Dictionary<FailureType, Handle<Material>> _failures = [];

    public Materials(IGpu gpu, Assets assets, TexturePools textures, PassGraph graph, Threads threads, CancellationToken stopping)
    {
        _assets = assets;
        _textures = textures;
        _graph = graph;
        _threads = threads;
        _stopping = stopping;
        Records = new GpuArray<GpuMaterial>(gpu, GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, 64);
        Gi = new GpuArray<GpuMaterialGi>(gpu, GpuBufferUsage.ComputeRead, 64);
        Bins = new GpuArray<uint>(gpu, GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, 64);
        _default = assets.Default<Material>();
        foreach (FailureType type in Enum.GetValues<FailureType>())
        {
            _failures[type] = assets.Failure<Material>(type);
            EntryOf(_failures[type]);
        }
    }

    public void Dispose()
    {
        Records.Dispose();
        Gi.Dispose();
        Bins.Dispose();
    }

    public GpuArray<GpuMaterial> Records { get; }

    /// <summary>
    /// What each material row gives global illumination: its colour and the light it gives off.
    /// </summary>
    public GpuArray<GpuMaterialGi> Gi { get; }

    /// <summary>
    /// How each material row is drawn: its raster and resolve bins, and whether it is in, transparent, masked or
    /// double-sided (<see cref="BinReady"/> and the rest); 0, not in, until it is.
    /// </summary>
    public GpuArray<uint> Bins { get; }

    /// <summary>
    /// The surfaces with a raster bin of their own, from <see cref="FixedRasterBins"/> on, in bin order: what their
    /// clusters clip by when they are masked, cards or fading.
    /// </summary>
    public List<SurfaceKey> RasterBins { get; } = [];

    /// <summary>
    /// The surfaces their pixels are shaded with, in resolve bin order: a surface and whether it is double-sided.
    /// </summary>
    public List<SurfaceKey> ResolveBins { get; } = [];

    /// <summary>
    /// The transparent surfaces drawn forward.
    /// </summary>
    public HashSet<SurfaceKey> TransparentSurfaces { get; } = [];

    /// <summary>
    /// The materials whose row, surface or type changed since the scene last asked: whose instances it draws anew.
    /// </summary>
    public HashSet<Handle<Material>> Changed { get; } = [];

    /// <summary>
    /// What a material slot is drawn with, once its material is in; none in the slot is the default material. Starts
    /// loading it the first time it is asked for, and is null while it loads.
    /// </summary>
    public DrawnMaterial? Get(Handle<Material> material)
    {
        return EntryOf(material).Drawn;
    }

    /// <summary>
    /// A material slot's row, whose bins say when it is in; starts loading it the first time it is asked for.
    /// </summary>
    public int RowOf(Handle<Material> material)
    {
        return EntryOf(material).Row;
    }

    /// <summary>
    /// Why a material slot is drawn as the failure, in a few words; null when it is not.
    /// </summary>
    public string? FailureOf(Handle<Material> material)
    {
        return _materials.TryGetValue(material == Handle<Material>.None ? _default : material, out MaterialEntry? entry) ? entry.Failure : null;
    }

    /// <summary>
    /// The material drawn in a broken one's place when <paramref name="type"/> broke: what a model that does not load
    /// is drawn with in every slot, for the mesh failure.
    /// </summary>
    public Handle<Material> Failure(FailureType type)
    {
        return _failures[type];
    }

    /// <summary>
    /// Whether the material in <paramref name="row"/> is drawn: in, its textures in and its pipelines compiled.
    /// </summary>
    public bool IsShown(int row) => (Bins.Rows[row] & BinReady) != 0;

    /// <summary>
    /// Lets the material go once no instance draws with it: its row and its variants' are freed, and the textures it
    /// samples that no other material does leave the pools, as do its cards' atlases no other variant has. It loads
    /// again if it is asked for again. The default and failure materials stay.
    /// </summary>
    public void Release(Handle<Material> material)
    {
        if (material == Handle<Material>.None || material == _default || _failures.ContainsValue(material) || !_materials.Remove(material, out MaterialEntry? entry))
        {
            return;
        }

        _rows.Free(entry.Row, 1);
        Bins.Set(entry.Row, 0);
        Changed.Remove(material);
        if (entry.Asset is not { } released)
        {
            return;
        }

        foreach (MaterialParam param in released.Params.Values)
        {
            if (param.Texture != Handle<Texture>.None && !IsSampled(param.Texture))
            {
                _textures.Release(param.Texture);
            }
        }
    }

    /// <summary>
    /// The material's file changed and it loaded again.
    /// </summary>
    public void Reload(Material material)
    {
        if (_materials.ContainsKey(material.Handle))
        {
            Take(material.Handle, material);
        }
    }

    /// <summary>
    /// A shader changed: a surface materials use is read again, no longer broken until it fails again, and its
    /// materials packed again.
    /// </summary>
    public void Reload(Shader shader)
    {
        _graph.BrokenSurfaces.Remove(shader.Handle);
        if (!_surfaces.Remove(shader.Handle))
        {
            return;
        }

        foreach ((Handle<Material> handle, MaterialEntry entry) in _materials)
        {
            if (entry.Asset is { } material && material.Shader == shader.Handle)
            {
                Take(handle, material);
            }
        }
    }

    /// <summary>
    /// Packs again the materials whose textures came in or failed or whose surface failed to compile, draws those whose
    /// pipelines are compiled now, then uploads what changed.
    /// </summary>
    public void Flush()
    {
        if (_textures.Changed.Count > 0 || _graph.Broke.Count > 0)
        {
            foreach ((Handle<Material> handle, MaterialEntry entry) in _materials)
            {
                if (entry.Asset is { } material && (UsesAny(material, _textures.Changed) || _graph.Broke.Contains(material.Shader)))
                {
                    Pack(handle, entry);
                }
            }

            _graph.Broke.Clear();
        }

        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            if (!_materials.TryGetValue(_waiting[i], out MaterialEntry? entry) || entry.Waiting is not { } waiting)
            {
                _waiting.RemoveAt(i);
                continue;
            }

            if (_graph.IsDrawable(waiting.Drawn.Surface, waiting.Drawn.Type))
            {
                Show(_waiting[i], entry, waiting);
                _waiting.RemoveAt(i);
            }
        }

        Records.Flush();
        Gi.Flush();
        Bins.Flush();
    }

    /// <summary>
    /// Whether a material still drawn samples <paramref name="texture"/>.
    /// </summary>
    private bool IsSampled(Handle<Texture> texture)
    {
        foreach (MaterialEntry entry in _materials.Values)
        {
            if (entry.Asset is not { } material)
            {
                continue;
            }

            foreach (MaterialParam param in material.Params.Values)
            {
                if (param.Texture == texture)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool UsesAny(Material material, HashSet<Handle<Texture>> textures)
    {
        foreach (MaterialParam param in material.Params.Values)
        {
            if (textures.Contains(param.Texture))
            {
                return true;
            }
        }

        return false;
    }

    private MaterialEntry EntryOf(Handle<Material> material)
    {
        if (material == Handle<Material>.None)
        {
            material = _default;
        }

        if (!_materials.TryGetValue(material, out MaterialEntry? entry))
        {
            entry = new MaterialEntry(_rows.Allocate(1));
            _materials[material] = entry;
            Bins.Set(entry.Row, 0);
            LoadAsync(material).FireAndForget();
        }

        return entry;
    }

    private async Task LoadAsync(Handle<Material> handle)
    {
        Result<Material> loaded = await _assets.LoadAsync(handle, _stopping).ConfigureAwait(false);
        if (!loaded.Ok)
        {
            Debugging.Error($"A material does not load, so it is drawn as the failure: {loaded.Error}");
        }

        _threads.Post(EngineThread.Render, () => Take(handle, loaded.Ok ? loaded.Value : null));
    }

    /// <summary>
    /// Takes in a material, or that it failed: its surface is read first if it is new, then the material packed.
    /// </summary>
    private void Take(Handle<Material> handle, Material? material)
    {
        if (!_materials.TryGetValue(handle, out MaterialEntry? entry))
        {
            return;
        }

        entry.Asset = material;
        if (material is not null && !_surfaces.ContainsKey(material.Shader))
        {
            _surfaces[material.Shader] = new SurfaceEntry();
            ReadSurfaceAsync(material.Shader).FireAndForget();
            return;
        }

        Pack(handle, entry);
    }

    private async Task ReadSurfaceAsync(Handle<Shader> surface)
    {
        Result<SurfaceSource> source = await SurfaceSource.ReadAsync(_assets, surface, _stopping).ConfigureAwait(false);
        if (!source.Ok)
        {
            Debugging.Error($"A surface cannot be drawn, so its materials are drawn as the failure: {source.Error}");
        }

        _threads.Post(EngineThread.Render, () => TakeSurface(surface, source.Ok ? source.Value : null));
    }

    private void TakeSurface(Handle<Shader> surface, SurfaceSource? source)
    {
        if (!_surfaces.TryGetValue(surface, out SurfaceEntry? entry))
        {
            return;
        }

        entry.Source = source;
        entry.IsRead = true;
        foreach ((Handle<Material> handle, MaterialEntry material) in _materials)
        {
            if (material.Asset is { } asset && asset.Shader == surface)
            {
                Pack(handle, material);
            }
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
            Break(handle, entry, FailureType.Shader, "Material does not load", doubleSided: false);
            return;
        }

        if (!_surfaces.TryGetValue(material.Shader, out SurfaceEntry? surface) || !surface.IsRead)
        {
            return;
        }

        if (surface.Source is not { } source || _graph.BrokenSurfaces.Contains(material.Shader))
        {
            Break(handle, entry, FailureType.Shader, "Surface does not compile", material.DoubleSided);
            return;
        }

        bool isLoading = false;
        foreach (MaterialParam param in material.Params.Values)
        {
            if (param.Texture != Handle<Texture>.None && _textures.HasFailed(param.Texture))
            {
                Break(handle, entry, FailureType.Texture, "Texture does not load", material.DoubleSided);
                return;
            }

            isLoading |= param.Texture != Handle<Texture>.None && _textures.RefOf(param.Texture) == TexturePools.None;
        }

        if (isLoading)
        {
            return;
        }

        foreach (string unknown in source.Layout.Unknown(material.Params.Keys))
        {
            Debugging.Warn($"{_assets.Path(handle)} sets {unknown}, which {source.Name}'s MaterialParams does not declare.");
        }

        GpuMaterial row = default;
        IReadOnlyDictionary<string, MaterialParam>?[] layers = [material.Params];
        source.Layout.Write(layers, param => _textures.RefOf(param.Texture), _ => null, MemoryMarshal.AsBytes(new Span<GpuMaterial>(ref row)));
        (Vector3 color, Vector3 emission) = source.Layout.GiOf(material.Params);
        GpuMaterialGi gi = new() { Color = new Vector4(color, 1), Emission = new Vector4(emission, 0) };
        DrawnMaterial drawn = new(entry.Row, new SurfaceKey(material.Shader, material.Type == MaterialType.Masked, material.DoubleSided), material.Type);
        entry.Broken = null;
        Draw(handle, entry, new PackedMaterial(row, gi, drawn, null));
    }

    /// <summary>
    /// Packs the material as the failure material for <paramref name="type"/>, in its own row, opaque, keeping its
    /// sides, once that material is drawn: until then it stays as it was, and is packed again when it is
    /// (<see cref="Show"/>). A failure material that is itself broken stands in for nothing.
    /// </summary>
    private void Break(Handle<Material> handle, MaterialEntry entry, FailureType type, string why, bool doubleSided)
    {
        entry.Broken = type;
        Handle<Material> failure = _failures[type];
        if (handle == failure || EntryOf(failure).Packed is not { } packed)
        {
            return;
        }

        DrawnMaterial drawn = new(entry.Row, packed.Drawn.Surface with { DoubleSided = doubleSided }, MaterialType.Opaque);
        Draw(handle, entry, packed with { Gi = _failureGi, Drawn = drawn, Failure = why });
    }

    /// <summary>
    /// Draws a material packed as soon as its pipelines are compiled: now, or once they are (<see cref="Flush"/>),
    /// drawn as it was until then.
    /// </summary>
    private void Draw(Handle<Material> handle, MaterialEntry entry, PackedMaterial packed)
    {
        if (_graph.IsDrawable(packed.Drawn.Surface, packed.Drawn.Type))
        {
            Show(handle, entry, packed);
            return;
        }

        if (entry.Waiting is null)
        {
            _waiting.Add(handle);
        }

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
        Records.Set(entry.Row, packed.Record);
        Gi.Set(entry.Row, packed.Gi);
        WriteBins(packed.Drawn);
        Changed.Add(handle);
        if (!_failures.ContainsValue(handle))
        {
            return;
        }

        foreach ((Handle<Material> broken, MaterialEntry waiting) in _materials)
        {
            if (broken != handle && waiting.Broken is { } type && _failures[type] == handle)
            {
                Pack(broken, waiting);
            }
        }
    }

    /// <summary>
    /// The row's bins word: the raster bin of its surface, the resolve bin of its surface and sides, made when they are
    /// first needed, and how it is drawn. A transparent material has neither; it is drawn forward.
    /// </summary>
    private void WriteBins(DrawnMaterial drawn)
    {
        SurfaceKey surface = drawn.Surface;
        uint flags = BinReady | (surface.Masked ? BinMasked : 0) | (surface.DoubleSided ? BinDoubleSided : 0);
        if (drawn.Type == MaterialType.Transparent)
        {
            TransparentSurfaces.Add(surface);
            Bins.Set(drawn.Row, flags | BinTransparent);
            return;
        }

        uint raster = (uint)(FixedRasterBins + BinOf(_rasterBins, RasterBins, surface, CardBins));
        uint resolve = (uint)BinOf(_resolveBins, ResolveBins, surface with { Masked = false }, MaxBins);
        Bins.Set(drawn.Row, flags | raster | (resolve << 16));

        // A surface past the bins there are is drawn with the last one's, and said: never left out.
        static int BinOf(Dictionary<SurfaceKey, int> bins, List<SurfaceKey> order, SurfaceKey key, int most)
        {
            if (bins.TryGetValue(key, out int bin))
            {
                return bin;
            }

            if (order.Count >= most - FixedRasterBins)
            {
                Debugging.Warn($"A frame has more than {most - FixedRasterBins} kinds of surface, so the rest are drawn with the last kind.");
                return order.Count - 1;
            }

            bins[key] = order.Count;
            order.Add(key);
            return order.Count - 1;
        }
    }

    private sealed class MaterialEntry(int row)
    {
        public int Row { get; } = row;

        public Material? Asset { get; set; }

        /// <summary>
        /// What it is drawn as; null until it is drawn.
        /// </summary>
        public PackedMaterial? Packed { get; set; }

        public DrawnMaterial? Drawn => Packed?.Drawn;

        /// <summary>
        /// What it was packed as last, while it waits for its pipelines to be drawn so; null when it does not wait.
        /// </summary>
        public PackedMaterial? Waiting { get; set; }

        /// <summary>
        /// What broke, when it is packed as a failure material or waits for one to be drawn; null when it is not broken.
        /// </summary>
        public FailureType? Broken { get; set; }

        /// <summary>
        /// Why it is drawn as the failure, in a few words; null when it is not.
        /// </summary>
        public string? Failure => Packed?.Failure;
    }

    /// <summary>
    /// A material packed: its row, what global illumination takes of it, what it is drawn with, and why it is drawn as the
    /// failure, null when it is not.
    /// </summary>
    private sealed record PackedMaterial(GpuMaterial Record, GpuMaterialGi Gi, DrawnMaterial Drawn, string? Failure);

    private sealed class SurfaceEntry
    {
        public SurfaceSource? Source { get; set; }

        public bool IsRead { get; set; }
    }
}

/// <summary>
/// One row of the materials buffer: a surface's <c>MaterialParams</c>, packed by its <see cref="ParamLayout"/>.
/// </summary>
[InlineArray(32)]
internal struct GpuMaterial
{
    private uint _word;
}

/// <summary>
/// What a material is drawn with: its row, the surface and variant its pipeline is composed of, and how it blends.
/// </summary>
internal readonly record struct DrawnMaterial(int Row, SurfaceKey Surface, MaterialType Type);

/// <summary>
/// A surface shader and the variant it is compiled as.
/// </summary>
internal readonly record struct SurfaceKey(Handle<Shader> Shader, bool Masked, bool DoubleSided);
