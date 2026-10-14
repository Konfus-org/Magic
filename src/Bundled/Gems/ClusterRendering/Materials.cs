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
/// broken is drawn with the failure surface, its kind saying what broke (Surfaces/Failure.surf.hlsl). Render thread.
/// </summary>
internal sealed class Materials : IDisposable
{
    /// <summary>
    /// The failure surface's kinds, as Surfaces/Failure.surf.hlsl numbers them.
    /// </summary>
    public const uint FailureShader = 1;
    public const uint FailureRecord = 2;
    public const uint FailureMesh = 3;
    public const uint FailureTexture = 4;

    public const string FailurePath = "Shaders/Surfaces/Failure.surf.hlsl";

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
    private readonly Threads _threads;
    private readonly CancellationToken _stopping;
    private readonly Dictionary<Handle<Material>, MaterialEntry> _materials = [];
    private readonly Dictionary<Handle<Shader>, SurfaceEntry> _surfaces = [];

    private readonly Dictionary<SurfaceKey, int> _rasterBins = [];
    private readonly Dictionary<SurfaceKey, int> _resolveBins = [];
    private readonly RangeAllocator _rows = new();
    private readonly Handle<Material> _default;

    public Materials(IGpu gpu, Assets assets, TexturePools textures, Threads threads, CancellationToken stopping)
    {
        _assets = assets;
        _textures = textures;
        _threads = threads;
        _stopping = stopping;
        Records = new GpuArray<GpuMaterial>(gpu, GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, 64);
        Gi = new GpuArray<GpuMaterialGi>(gpu, GpuBufferUsage.ComputeRead, 64);
        Bins = new GpuArray<uint>(gpu, GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, 64);
        Failure = assets.Find<Shader>(FailurePath);
        if (Failure == Handle<Shader>.None)
        {
            Debugging.Error($"There is no failure surface at {FailurePath}, so broken materials are not drawn.");
        }

        _default = assets.Default<Material>();
        FailedMesh = new DrawnMaterial(FailureRow(FailureMesh), new SurfaceKey(Failure, false, false), MaterialType.Opaque);
        WriteBins(FailedMesh);
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
    /// The failure surface, which a broken material is drawn with.
    /// </summary>
    public Handle<Shader> Failure { get; }

    /// <summary>
    /// What a renderer whose model does not load is drawn with.
    /// </summary>
    public DrawnMaterial FailedMesh { get; }

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
    /// Lets the material go once no instance draws with it: its row and its variants' are freed, and the textures it
    /// samples that no other material does leave the pools, as do its cards' atlases no other variant has. It loads
    /// again if it is asked for again. The default material stays.
    /// </summary>
    public void Release(Handle<Material> material)
    {
        if (material == Handle<Material>.None || material == _default || !_materials.Remove(material, out MaterialEntry? entry))
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
    /// A shader changed: a surface materials use is read again, and its materials packed again.
    /// </summary>
    public void Reload(Shader shader)
    {
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
    /// Packs again the materials whose textures came in or failed, then uploads what changed.
    /// </summary>
    public void Flush()
    {
        if (_textures.Changed.Count > 0)
        {
            foreach ((Handle<Material> handle, MaterialEntry entry) in _materials)
            {
                if (entry.Asset is { } material && UsesAny(material, _textures.Changed))
                {
                    Pack(handle, entry);
                }
            }

            _textures.Changed.Clear();
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
    /// Writes a material's row and what it is drawn with: its surface's packing of its params, or the failure's kind
    /// when it, its surface or a texture it samples is broken. A texture still loading packs as none until it is in.
    /// </summary>
    private void Pack(Handle<Material> handle, MaterialEntry entry)
    {
        if (entry.Asset is not { } material)
        {
            Use(handle, entry, FailureRecord, MaterialType.Opaque, doubleSided: false);
            return;
        }

        if (!_surfaces.TryGetValue(material.Shader, out SurfaceEntry? surface) || !surface.IsRead)
        {
            return;
        }

        if (surface.Source is not { } source)
        {
            Use(handle, entry, FailureShader, material.Type, material.DoubleSided);
            return;
        }

        foreach (MaterialParam param in material.Params.Values)
        {
            if (param.Texture != Handle<Texture>.None && _textures.HasFailed(param.Texture))
            {
                Use(handle, entry, FailureTexture, material.Type, material.DoubleSided);
                return;
            }
        }

        foreach (string unknown in source.Layout.Unknown(material.Params.Keys))
        {
            Debugging.Warn($"{_assets.Path(handle)} sets {unknown}, which {source.Name}'s MaterialParams does not declare.");
        }

        GpuMaterial row = default;
        IReadOnlyDictionary<string, MaterialParam>?[] layers = [material.Params];
        source.Layout.Write(layers, param => _textures.RefOf(param.Texture), MemoryMarshal.AsBytes(new Span<GpuMaterial>(ref row)));
        Records.Set(entry.Row, row);
        (Vector3 color, Vector3 emission) = source.Layout.GiOf(material.Params);
        Gi.Set(entry.Row, new GpuMaterialGi { Color = new Vector4(color, 1), Emission = new Vector4(emission, 0) });
        entry.Failure = null;
        Set(handle, entry, new DrawnMaterial(entry.Row, new SurfaceKey(material.Shader, material.Type == MaterialType.Masked, material.DoubleSided), material.Type));
    }

    /// <summary>
    /// Draws the material as the failure of <paramref name="kind"/>, keeping its blending and sides.
    /// </summary>
    private void Use(Handle<Material> handle, MaterialEntry entry, uint kind, MaterialType type, bool doubleSided)
    {
        entry.Failure = kind switch
        {
            FailureShader => "Surface does not compile",
            FailureTexture => "Texture does not load",
            _ => "Material does not load",
        };
        Records.Set(entry.Row, FailureRecordOf(kind));
        Gi.Set(entry.Row, _failureGi);
        Set(handle, entry, new DrawnMaterial(entry.Row, new SurfaceKey(Failure, false, doubleSided), type == MaterialType.Transparent ? MaterialType.Opaque : type));
    }

    /// <summary>
    /// What the material is drawn with, its row just written, and the bins it is drawn by.
    /// </summary>
    private void Set(Handle<Material> handle, MaterialEntry entry, DrawnMaterial drawn)
    {
        WriteBins(drawn);
        if (entry.Drawn != drawn)
        {
            entry.Drawn = drawn;
            Changed.Add(handle);
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

    /// <summary>
    /// A row of its own for a failure kind, which no material file is.
    /// </summary>
    private int FailureRow(uint kind)
    {
        int row = _rows.Allocate(1);
        Records.Set(row, FailureRecordOf(kind));
        Gi.Set(row, _failureGi);
        return row;
    }

    /// <summary>
    /// The failure surface's record: its one param, the kind.
    /// </summary>
    private static GpuMaterial FailureRecordOf(uint kind)
    {
        GpuMaterial row = default;
        row[0] = kind;
        return row;
    }

    private sealed class MaterialEntry(int row)
    {
        public int Row { get; } = row;

        public Material? Asset { get; set; }

        public DrawnMaterial? Drawn { get; set; }

        /// <summary>
        /// Why it is drawn as the failure, in a few words; null when it is not.
        /// </summary>
        public string? Failure { get; set; }
    }

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
