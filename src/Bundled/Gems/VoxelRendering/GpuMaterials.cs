using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// A surface shader and the variant it is compiled as.
/// </summary>
internal readonly record struct SurfaceKey(Handle<Shader> Shader, bool IsMasked, bool IsDoubleSided);

/// <summary>
/// What a material is drawn with: its row, the surface and variant its pipelines are composed of, and how it blends; or,
/// when its shader is a sky, the sky shader the sky's passes are composed of, which no mesh draws.
/// </summary>
internal readonly record struct DrawnMaterial(int Row, SurfaceKey Surface, MaterialType Type, bool IsSky);

/// <summary>
/// The materials resident on the GPU, as rows of the materials' table (<see cref="GpuTables.Materials"/>) a surface's
/// generated <c>LoadMaterialParams</c> reads, packed by its surface shader's params, which are read once. A row also
/// says how its material is drawn (<see cref="GpuTables.MaterialBins"/>): the raster bin its clusters are drawn into the
/// visibility buffer by when they clip (a masked surface's, or a card's or a fade's of any), the resolve bin its pixels
/// are shaded into the G-buffer by, and whether it is in yet, transparent, masked or double-sided. What is kept is what
/// is held, as the other residents are; a material holds the textures it samples. What is broken is packed, in its own
/// row, as the failure material for what broke. A material is drawn only once it can be drawn as it should be: its
/// textures in, and every pipeline it is drawn with made (<see cref="GpuPipelines.IsDrawable"/>); until then its row
/// stays as it was. A material whose params, blending or sides a game set is taken in again as one whose file changed
/// is, from the count its params keep (<see cref="MaterialParamDictionary.Version"/>), and its instances are told only
/// when what they work out from it changed (<see cref="Changed"/>). The default material and the failure materials are
/// held from the start and never leave. Render thread.
/// </summary>
internal sealed class GpuMaterials
{
    /// <summary>
    /// The raster bins that do not clip, single-sided then double-sided: a surface's own bins come after them.
    /// </summary>
    internal const int FixedRasterBins = 2;

    /// <summary>
    /// The raster bins a phase draws: the fixed ones, a surface's own from <see cref="FixedRasterBins"/>, and its cards'
    /// <see cref="CardBinOffset"/> after its own, as Shaders/Rendering/Include/Culling.hlsli numbers them.
    /// </summary>
    internal const int RasterBinRows = 256;

    internal const int CardBinOffset = 126;

    /// <summary>
    /// The surfaces with raster bins of their own at most: each one's card bin, <see cref="CardBinOffset"/> after its
    /// own, still among the <see cref="RasterBinRows"/>.
    /// </summary>
    private const int SurfaceRasterBins = 126;

    /// <summary>
    /// A row's bins word: its raster bin in the low byte, its resolve bin in the third, and these.
    /// </summary>
    private const uint BinMasked = 1u << 8;
    private const uint BinDoubleSided = 1u << 9;
    private const uint BinTransparent = 1u << 10;
    private const uint BinReady = 1u << 11;

    /// <summary>
    /// The most resolve bins there are, as a byte numbers them.
    /// </summary>
    private const int MaxResolveBins = 256;

    private readonly GpuTables _tables;
    private readonly GpuTextures _textures;
    private readonly GpuPipelines _pipelines;
    private readonly Assets _assets;
    private readonly FailureHandler _failures;
    private readonly CancellationToken _stopping;
    private readonly Resident<Material, MaterialEntry, Material> _materials;
    private readonly Dictionary<Handle<Shader>, SurfaceEntry> _surfaces = [];

    /// <summary>
    /// Surfaces read, or that could not be, for the render thread to take in as <see cref="Take"/> starts.
    /// </summary>
    private readonly Channel<(Handle<Shader> Surface, SurfaceSource? Source)> _surfacesRead =
        Channel.CreateUnbounded<(Handle<Shader> Surface, SurfaceSource? Source)>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The materials packed and waiting for their pipelines to be made (<see cref="MaterialEntry.Waiting"/>), or let go
    /// since: those are passed over.
    /// </summary>
    private readonly List<Handle<Material>> _waiting = [];

    private readonly Dictionary<SurfaceKey, int> _rasterBins = [];
    private readonly Dictionary<SurfaceKey, int> _resolveBins = [];
    private readonly RangeAllocator _rows = new();
    private readonly Handle<Material> _defaultMaterial;

    /// <summary>
    /// <see cref="GetTextureRef"/>, made once, so packing a material, as often as a game sets one, makes no closure.
    /// </summary>
    private readonly Func<MaterialParam, uint> _getTextureRef;

    /// <summary>
    /// The surfaces that broke since the materials last asked, whose materials are packed anew.
    /// </summary>
    private readonly HashSet<Handle<Shader>> _broke = [];

    /// <summary>
    /// The world's sky material, held while it is the world's, and the one shown, held while it is: the world's once it
    /// is in, until then the one shown before.
    /// </summary>
    private Handle<Material> _sky;
    private Handle<Material> _shownSky;

    internal GpuMaterials(
        Assets assets,
        GpuTables tables,
        GpuTextures textures,
        GpuPipelines pipelines,
        FailureHandler failures,
        CancellationToken stopping)
    {
        _assets = assets;
        _tables = tables;
        _textures = textures;
        _pipelines = pipelines;
        _failures = failures;
        _stopping = stopping;
        _getTextureRef = GetTextureRef;
        _materials = new(assets, "A material does not load, so it is drawn as the failure", Task.FromResult, stopping);
        _defaultMaterial = assets.GetDefault<Material>();
        Record(_defaultMaterial);
        foreach (Handle<Material> failure in failures.Materials)
        {
            Record(failure);
        }
    }

    /// <summary>
    /// The materials that came in since the instances last asked, or whose row changed what their instances work out
    /// from it: why it is drawn as the failure, whether it is a sky, whether it is transparent.
    /// </summary>
    internal HashSet<Handle<Material>> Changed { get; } = [];

    /// <summary>
    /// The surfaces with a raster bin of their own, from <see cref="FixedRasterBins"/> on, in bin order: what their
    /// clusters clip by when they are masked, cards or fading.
    /// </summary>
    internal List<SurfaceKey> RasterBins { get; } = [];

    /// <summary>
    /// The surfaces their pixels are shaded with, in resolve bin order: a surface and whether it is double-sided.
    /// </summary>
    internal List<SurfaceKey> ResolveBins { get; } = [];

    /// <summary>
    /// Holds <paramref name="material"/>, none being the default material, starting its load the first time: its row,
    /// whose bins say when it is in.
    /// </summary>
    internal int Record(Handle<Material> material)
    {
        if (material == Handle<Material>.None) material = _defaultMaterial;

        return _materials.Record(
            material,
            () =>
            {
                MaterialEntry entry = new(_rows.Allocate(1));
                _tables.MaterialBins.Set(entry.Row, 0);

                return entry;
            }).Row;
    }

    internal void Release(Handle<Material> material)
        => _materials.Release(material == Handle<Material>.None ? _defaultMaterial : material);

    /// <summary>
    /// Lets go of every material nothing holds any more, before the models and textures it holds.
    /// </summary>
    internal void Purge() => _materials.Purge(Evict);

    /// <summary>
    /// Takes in the materials loaded and the surfaces read since the last frame, packs again the materials whose
    /// textures came in or failed or whose surface failed to compile, and draws those whose pipelines are made now. After
    /// the textures are taken in, as materials are packed with them.
    /// </summary>
    internal void Take()
    {
        while (_surfacesRead.Reader.TryRead(out (Handle<Shader> Surface, SurfaceSource? Source) read))
        {
            TakeSurface(read.Surface, read.Source);
        }

        _materials.Take(TakeMaterial);
        FollowChanges();

        if (_failures.TakeBroke(_broke) || _textures.Changed.Count > 0)
        {
            foreach ((Handle<Material> handle, MaterialEntry entry) in _materials.Entries)
            {
                if (entry.Asset is { } material
                    && (IsUsingAnyOf(entry, _textures.Changed) || _broke.Contains(material.Shader)))
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
    /// The material's file changed and it loaded again.
    /// </summary>
    internal void Reload(Material material) => _materials.Reload(material);

    /// <summary>
    /// A shader changed: a surface materials use is read again, no longer broken until it fails again, and its
    /// materials packed again once it is.
    /// </summary>
    internal void Reload(Shader shader)
    {
        _failures.Repair(shader.Handle);

        if (!_surfaces.ContainsKey(shader.Handle)) return;

        _surfaces[shader.Handle] = new SurfaceEntry();
        ReadSurfaceAsync(shader.Handle).FireAndForget();
    }

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
    /// Whether a held material is drawn: in, its textures in and its pipelines made.
    /// </summary>
    internal bool IsShown(Handle<Material> material)
    {
        Handle<Material> held = material == Handle<Material>.None ? _defaultMaterial : material;

        return _materials.Entries.TryGetValue(held, out MaterialEntry? entry)
            && (_tables.MaterialBins.Rows[entry.Row] & BinReady) != 0;
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
    /// shader once it is in, the material held while it is the world's, and the sky shown before it until then; the
    /// renderer's own fallback sky without one, or before any is shown; and the sky failure material when the material
    /// is no sky, its shader a surface or a sky that does not compile. Why is said where failures are.
    /// </summary>
    internal (SurfaceKey Surface, int Row) GetSky(Handle<Material>? world)
    {
        Handle<Material> material = world ?? Handle<Material>.None;
        if (material != _sky)
        {
            if (material != Handle<Material>.None && material != _shownSky) Record(material);

            if (_sky != Handle<Material>.None && _sky != _shownSky) Release(_sky);

            _sky = material;
        }

        if (_sky != _shownSky && (_sky == Handle<Material>.None || Get(_sky) is not null))
        {
            if (_shownSky != Handle<Material>.None) Release(_shownSky);

            _shownSky = _sky;
        }

        DrawnMaterial? drawn = _shownSky == Handle<Material>.None ? null : Get(_shownSky);
        Handle<Material> broken = drawn is { IsSky: false } ? _shownSky : Handle<Material>.None;
        _failures.SayBrokenSky(
            broken,
            broken == Handle<Material>.None ? string.Empty : GetFailure(broken) ?? "a surface, not a sky");
        if (broken != Handle<Material>.None) drawn = Get(_failures.GetMaterial(FailureType.Sky));

        return drawn is { IsSky: true } sky ? (sky.Surface, sky.Row) : (default, 0);
    }

    private static bool IsUsingAnyOf(MaterialEntry entry, HashSet<Handle<Texture>> textures)
    {
        foreach (Handle<Texture> texture in entry.Textures)
        {
            if (textures.Contains(texture)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether what the instances drawing a material work out from it differs between two of its packings: why it is
    /// drawn as the failure, whether it is a sky, whether it is transparent.
    /// </summary>
    private static bool IsLookChanged(PackedMaterial before, PackedMaterial after)
    {
        bool wasTransparent = before.Drawn.Type == MaterialType.Transparent;
        bool isTransparent = after.Drawn.Type == MaterialType.Transparent;

        return !string.Equals(before.Failure, after.Failure, StringComparison.Ordinal)
            || before.Drawn.IsSky != after.Drawn.IsSky
            || wasTransparent != isTransparent;
    }

    /// <summary>
    /// Whether every pipeline a material is drawn with is made: a sky's are the sky's passes', which wait for it
    /// themselves.
    /// </summary>
    private bool IsDrawable(DrawnMaterial drawn) => drawn.IsSky || _pipelines.IsDrawable(drawn.Surface);

    /// <summary>
    /// The texture a param names as a surface names it.
    /// </summary>
    private uint GetTextureRef(MaterialParam param) => _textures.GetRef(param.Texture);

    /// <summary>
    /// Takes in again each material held whose params, blending or sides a game set since it was last read, as one
    /// whose file changed is, so it is drawn so from this frame.
    /// </summary>
    private void FollowChanges()
    {
        foreach ((Handle<Material> handle, MaterialEntry entry) in _materials.Entries)
        {
            if (entry.Asset is { } material && entry.Read != MaterialRead.Of(material))
                TakeMaterial(handle, entry, material);
        }
    }

    /// <summary>
    /// Takes in a material, or that it failed: what of it is read noted first, the textures it samples held in place of
    /// those it sampled before, its surface read first if it is new, then the material packed.
    /// </summary>
    private void TakeMaterial(Handle<Material> handle, MaterialEntry entry, Material? material)
    {
        entry.Read = material is null ? default : MaterialRead.Of(material);
        HoldTextures(entry, material);
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
    /// Holds every texture <paramref name="material"/> samples now, then lets go of those the entry held for it before,
    /// so one it still samples never leaves between.
    /// </summary>
    private void HoldTextures(MaterialEntry entry, Material? material)
    {
        Handle<Texture>[] before = entry.Textures;
        List<Handle<Texture>> now = [];
        foreach (KeyValuePair<string, MaterialParam> param in material?.Params ?? [])
        {
            if (param.Value.Texture == Handle<Texture>.None) continue;

            _textures.Record(param.Value.Texture);
            now.Add(param.Value.Texture);
        }

        entry.Textures = [.. now];
        foreach (Handle<Texture> texture in before)
        {
            _textures.Release(texture);
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
    /// surface or a texture it samples is; drawn once its pipelines are made. Nothing while a texture it samples is
    /// loading: it is packed again once the texture is in.
    /// </summary>
    private void Pack(Handle<Material> handle, MaterialEntry entry)
    {
        if (entry.Asset is not { } material)
        {
            PackAsFailure(handle, entry, FailureType.Shader, "Material does not load", isDoubleSided: false);

            return;
        }

        if (!_surfaces.TryGetValue(material.Shader, out SurfaceEntry? surface) || !surface.IsRead) return;

        if (surface.Source is not { } source || _failures.IsBroken(material.Shader))
        {
            PackAsFailure(handle, entry, FailureType.Shader, "Surface does not compile", material.IsDoubleSided);

            return;
        }

        (bool hasFailedTexture, bool isLoading) = CheckTextures(material);
        if (hasFailedTexture)
        {
            PackAsFailure(handle, entry, FailureType.Texture, "Texture does not load", material.IsDoubleSided);

            return;
        }

        if (isLoading) return;

        foreach (string unknown in source.Layout.FindUnknown(material.Params.Keys))
        {
            if (!entry.Unknown.Add(unknown)) continue;

            Debugging.Warn(
                $"{_assets.GetPath(handle)} sets {unknown}, which {source.Name}'s MaterialParams does not declare.");
        }

        GpuMaterial row = default;
        source.Layout.Write(material.Params, _getTextureRef, MemoryMarshal.AsBytes(new Span<GpuMaterial>(ref row)));
        DrawnMaterial drawn = new(
            entry.Row,
            new SurfaceKey(material.Shader, material.Type == MaterialType.Masked, material.IsDoubleSided),
            material.Type,
            source.IsSky);
        ShowWhenDrawable(handle, entry, new PackedMaterial(row, drawn, null));
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

            if (_textures.HasFailed(param.Texture)) return (true, false);

            isLoading |= _textures.GetRef(param.Texture) == GpuTextures.None;
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
        bool isDoubleSided)
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
            packed.Drawn.Surface with { IsDoubleSided = isDoubleSided },
            MaterialType.Opaque,
            IsSky: false);
        ShowWhenDrawable(handle, entry, packed with { Drawn = drawn, Failure = why });
    }

    /// <summary>
    /// Draws a material packed as soon as every pipeline it is drawn with is made: now, or once they are
    /// (<see cref="Take"/>), drawn as it was until then.
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
    /// Writes a material's row as packed and the bins it is drawn by, and tells its instances when what they work out
    /// from it changed. A failure material drawn packs again the broken materials waiting for it.
    /// </summary>
    private void Show(Handle<Material> handle, MaterialEntry entry, PackedMaterial packed)
    {
        PackedMaterial? shown = entry.Packed;
        entry.Waiting = null;
        entry.Packed = packed;
        _tables.Materials.Set(entry.Row, packed.Record);
        WriteBins(packed.Drawn);
        if (shown is null || IsLookChanged(shown, packed)) Changed.Add(handle);

        if (!_failures.IsMaterial(handle)) return;

        foreach ((Handle<Material> broken, MaterialEntry waiting) in _materials.Entries)
        {
            if (broken != handle && waiting.Broken is { } type && _failures.GetMaterial(type) == handle)
                Pack(broken, waiting);
        }
    }

    /// <summary>
    /// The row's bins word: the raster bin of its surface, the resolve bin of its surface and sides, made when they are
    /// first needed, and how it is drawn. A transparent material has neither; it is drawn forward. A sky has neither
    /// either, as no mesh draws it.
    /// </summary>
    private void WriteBins(DrawnMaterial drawn)
    {
        if (drawn.IsSky)
        {
            _tables.MaterialBins.Set(drawn.Row, BinReady);

            return;
        }

        SurfaceKey surface = drawn.Surface;
        uint flags = BinReady | (surface.IsMasked ? BinMasked : 0) | (surface.IsDoubleSided ? BinDoubleSided : 0);
        if (drawn.Type == MaterialType.Transparent)
        {
            _tables.MaterialBins.Set(drawn.Row, flags | BinTransparent);

            return;
        }

        uint raster = (uint)(FixedRasterBins + BinOf(_rasterBins, RasterBins, surface, SurfaceRasterBins));
        uint resolve = (uint)BinOf(_resolveBins, ResolveBins, surface with { IsMasked = false }, MaxResolveBins);
        _tables.MaterialBins.Set(drawn.Row, flags | raster | (resolve << 16));

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
        _rows.Free(entry.Row, 1);
        _tables.MaterialBins.Set(entry.Row, 0);
        Changed.Remove(material);
        HoldTextures(entry, null);
    }

    /// <summary>
    /// What a material entry last read of its material: its params' count, read first, as what is read after it is at
    /// least as new, then how it is blended and its sides. Another of any is a change to take in.
    /// </summary>
    private readonly record struct MaterialRead(long Version, MaterialType Type, bool IsDoubleSided)
    {
        internal static MaterialRead Of(Material material)
        {
            long version = material.Params.Version;

            return new MaterialRead(version, material.Type, material.IsDoubleSided);
        }
    }

    private sealed class MaterialEntry(int row)
    {
        internal int Row { get; } = row;

        internal Material? Asset { get; set; }

        /// <summary>
        /// What of <see cref="Asset"/> it was last taken in from; a game setting any of it since is taken in again.
        /// </summary>
        internal MaterialRead Read { get; set; }

        /// <summary>
        /// The textures its material sampled when it was last taken in, which it holds.
        /// </summary>
        internal Handle<Texture>[] Textures { get; set; } = [];

        /// <summary>
        /// The params its material sets that its surface does not declare, each said once, however often it is packed.
        /// </summary>
        internal HashSet<string> Unknown { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// What it is drawn as; null until it is drawn.
        /// </summary>
        internal PackedMaterial? Packed { get; set; }

        internal DrawnMaterial? Drawn => Packed?.Drawn;

        /// <summary>
        /// What it was packed as last, while it waits for its pipelines to be made; null when it does not wait.
        /// </summary>
        internal PackedMaterial? Waiting { get; set; }

        /// <summary>
        /// What broke, when it is packed as a failure material or waits for one to be drawn; null when it is not broken.
        /// </summary>
        internal FailureType? Broken { get; set; }

        /// <summary>
        /// Why it is drawn as the failure, in a few words; null when it is not.
        /// </summary>
        internal string? Failure => Packed?.Failure;
    }

    /// <summary>
    /// A material packed: its row, what it is drawn with, and why it is drawn as the failure, null when it is not.
    /// </summary>
    private sealed record PackedMaterial(GpuMaterial Record, DrawnMaterial Drawn, string? Failure);

    private sealed class SurfaceEntry
    {
        internal SurfaceSource? Source { get; set; }

        internal bool IsRead { get; set; }
    }
}
