using System.Collections.Immutable;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.ShadowMaps;

/// <summary>
/// What the cascade fit takes for each view of the frame: the view as the GPU holds it.
/// </summary>
public readonly record struct CascadeFitInput(GpuView View);

/// <summary>
/// What the sun's resolve takes for each view: the view, its cascades and their depth.
/// </summary>
public readonly record struct SunShadowInput(GpuView View, CascadeViews Cascades, CascadeDepth Depth);

/// <summary>
/// What the local lights' fit takes once a frame: the world, with its lights and main camera, and the instances that
/// changed.
/// </summary>
public readonly record struct LocalFitInput(GpuWorld World, GpuInstances Instances);

/// <summary>
/// What the local lights' shadows take once a frame: their tiles and the atlas with their depth drawn.
/// </summary>
public readonly record struct LocalResolveInput(LocalShadowViews Views, LocalShadowDepth Depth);

/// <summary>
/// What the fit of the sun's reflective shadow map takes for each view: the view as the GPU holds it.
/// </summary>
public readonly record struct BounceFitInput(GpuView View);

/// <summary>
/// What the bounce light's gather takes for each view: the view, its reflective shadow map with the lit surfaces drawn
/// into it, and the sky's tables, which set the sun's light.
/// </summary>
public readonly record struct BounceInput(GpuView View, BounceViews Views, BounceMaps Maps, SkyTables? Sky);

/// <summary>
/// Fits the sun's cascades to each view, and shadows each pixel of the view from their depth, into the frame's
/// <see cref="ShadowMask"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each frame it splits the view into cascades from the near plane to the shadow distance, fits a sphere around each
/// cascade's slice of the view, and gives each the sphere's square from the sun, snapped to whole texels, so a shadow
/// keeps still as the camera turns and moves. A geometry gem draws their depth. The resolve then writes the sun's
/// slot of the mask and the ids of the mask into the frame's buffer.
/// </para>
/// </remarks>
/// <para>
/// Once a frame it also fits the local lights' tiles (<see cref="LocalTiles"/>), copies the tiles whose still depth the
/// cache holds into the local atlas, and clears those that the cache draws again. After a geometry gem draws their
/// depth, it builds the atlas's blocker mips and gives shading <see cref="LocalShadows"/>.
/// </para>
public sealed class Renderer
    : IRender<CascadeFitInput, CascadeViews>,
        IRender<SunShadowInput, ShadowMask>,
        IRender<LocalFitInput, LocalShadowViews>,
        IRender<LocalResolveInput, LocalShadows>,
        IRender<BounceFitInput, BounceViews>,
        IRender<BounceInput, BounceLight>
{
    /// <summary>
    /// The most cascades a view has: two by two in the atlas.
    /// </summary>
    private const int MaxCascades = 4;

    private const int BytesPerPixel = 16;

    /// <summary>
    /// The levels of the blocker mips, from half the atlas to a sixteenth.
    /// </summary>
    private const int BlockerMipLevels = 4;

    /// <summary>
    /// The most steps of the contact shadow's march.
    /// </summary>
    private const int MostContactSteps = 64;

    /// <summary>
    /// The metres towards the sun from a cascade's centre that its near plane is at, so every caster up to there is
    /// drawn where it is. A caster further off is drawn at the near plane.
    /// </summary>
    private const float CasterReach = 1000;

    /// <summary>
    /// The steps a cascade's radius is rounded up to, in metres, so that the rounding of the sphere's fit does not
    /// change its size from frame to frame.
    /// </summary>
    private const float RadiusStep = 1f / 16;

    // A name for each level of the blocker mips: GPU timings are kept by name.
    private static readonly ImmutableArray<string> _blockerMipPasses =
    [
        "Build blocker mips 0",
        "Build blocker mips 1",
        "Build blocker mips 2",
        "Build blocker mips 3",
    ];

    private static readonly ImmutableArray<string> _localMipPasses =
    [
        "Build local blocker mips 0",
        "Build local blocker mips 1",
        "Build local blocker mips 2",
        "Build local blocker mips 3",
    ];

    private readonly LocalTiles _localTiles = new();

    private readonly IndexedDraw[] _tileDraws = new IndexedDraw[LocalTiles.MaxTiles];

    private readonly DrawShaders _copyShaders;

    private readonly ShadowMapsSettings _settings;

    private readonly IGraphicsApi _gpu;

    private readonly ShaderEntry _resolveShader;

    private readonly ShaderEntry _blockerMipShader;

    private readonly ShaderEntry _gatherShader;

    private readonly ShaderEntry _bounceBlurShader;

    private readonly ImmutableArray<ShaderTypeArgument> _openSky;

    internal Renderer(ShadowMapsSettings settings, AssetManager assets, IGraphicsApi gpu)
    {
        _settings = settings;
        _gpu = gpu;
        _resolveShader = PassShaders.GetEntry(assets, "ResolveSunShadow", "ResolveSunShadow");
        _blockerMipShader = PassShaders.GetEntry(assets, "BuildBlockerMips", "BuildBlockerMips");
        _gatherShader = PassShaders.GetEntry(assets, "GatherBounce", "GatherBounce");
        _bounceBlurShader = PassShaders.GetEntry(assets, "GatherBounce", "BlurBounce");
        _openSky = SkyTables.GetOpenSky(assets);
        _copyShaders = new(
            PassShaders.GetEntry(assets, "CopyShadowTiles", "CopyShadowTileVertex", ShaderStage.Vertex),
            PassShaders.GetEntry(assets, "CopyShadowTiles", "CopyShadowTilePixel", ShaderStage.Pixel)
        )
        {
            Depth = DepthMode.Always,
            Cull = CullMode.None,
        };
    }

    /// <summary>
    /// Fits the sun's cascades to the view of this turn, and uploads their rows.
    /// </summary>
    /// <returns>
    /// The view's cascades, or none while shadow maps are off or their rows are not made.
    /// </returns>
    public CascadeViews? Render(in RenderFrame frame, RenderGraph graph, in CascadeFitInput input)
    {
        if (!_settings.IsShadowMapsOn)
            return null;

        int count = Math.Clamp(_settings.CascadeCount, 1, MaxCascades);
        int resolution = Math.Max(_settings.CascadeResolution, 1);
        Result<GpuBuffer> views = _gpu.GetBuffer(new("Cascade views", MaxCascades * GpuShadowView.Bytes), frame.View);
        if (!views.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Shadows are not drawn", views.Error);

            return null;
        }

        Span<GpuShadowView> rows = graph.Upload<GpuShadowView>(views.Value, count);
        if (rows.Length < count)
            return null;

        FitCascades(input.View, resolution, rows);

        return new CascadeViews(views.Value, count, resolution * 2);
    }

    /// <summary>
    /// Shadows each pixel of the view of this turn from its cascades' depth, and writes the mask's ids into the frame's
    /// buffer.
    /// </summary>
    /// <returns>
    /// The view's mask, or none while the shader builds or what it writes is not made.
    /// </returns>
    public ShadowMask? Render(in RenderFrame frame, RenderGraph graph, in SunShadowInput input)
    {
        GpuState state = _gpu.GetState(_resolveShader, _blockerMipShader);
        if (state == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "Shadows are not resolved", "a shader of the sun's shadow was not built.");

        if (state != GpuState.Ready)
            return null;

        GpuView view = input.View;
        GpuTexture atlas = input.Depth.Atlas;
        int mipSize = Math.Max(input.Cascades.AtlasSize / 2, 1);
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Sun shadow constants", Unsafe.SizeOf<SunShadowConstants>()),
            frame.View
        );
        Result<GpuBuffer> mask = _gpu.GetBuffer(
            new("Shadow mask", (long)view.Width * view.Height * BytesPerPixel),
            frame.View
        );
        Result<GpuTexture> minMips = _gpu.GetTexture(
            new("Cascade min mips", mipSize, mipSize, GpuFormat.R32Sfloat, Levels: BlockerMipLevels),
            frame.View
        );
        Result<GpuTexture> maxMips = _gpu.GetTexture(
            new("Cascade max mips", mipSize, mipSize, GpuFormat.R32Sfloat, Levels: BlockerMipLevels),
            frame.View
        );
        Result<GpuTexture> details = _gpu.GetTexture(
            new("Sun shadow details", view.Width, view.Height, GpuFormat.R8G8B8A8Unorm),
            frame.View
        );
        Result<GpuSampler> sampler = _gpu.GetSampler(
            new SamplerDescription { Filter = TextureFilter.Nearest, Wrap = TextureWrap.Clamp }
        );
        Result asked = Result.FindFailure(constants, mask, minMips, maxMips, details, sampler);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Shadows are not resolved", asked.Error);

            return null;
        }

        SunShadowConstants resolve = new()
        {
            Views = input.Cascades.Views.Id,
            ViewCount = (uint)input.Cascades.Count,
            Atlas = atlas.Id,
            Sampler = sampler.Value.Id,
            MinMips = minMips.Value.Id,
            MaxMips = maxMips.Value.Id,
            MipLevels = BlockerMipLevels,
            Mask = mask.Value.Id,
            Details = details.Value.Id,
            NormalBias = _settings.NormalBias,
            CascadeBlend = Math.Clamp(_settings.CascadeBlend, 0, 0.5f),
            BlockerTaps = (uint)Math.Clamp(_settings.BlockerTaps, 1, 64),
            FilterTaps = (uint)Math.Clamp(_settings.FilterTaps, 1, 64),
            MaxPenumbra = Math.Clamp(_settings.MaxPenumbraTexels, 1, ShadowMask.WidestPenumbra),
            ContactLength = Math.Max(_settings.ContactShadowLength, 0),
            ContactSteps = _settings.IsContactShadowsOn
                ? (uint)Math.Clamp(_settings.ContactShadowSteps, 1, MostContactSteps)
                : 0,
            ContactThickness = Math.Max(_settings.ContactShadowThickness, 0),
        };
        // Not staged: nothing is recorded, and the frame's row stays the view's default, which the shaders read as none.
        GpuShadows row = new()
        {
            Mask = mask.Value.Id,
            CascadeAtlas = atlas.Id,
            SunDetails = details.Value.Id,
        };
        if (!graph.Upload(constants.Value, [resolve]) || !graph.Upload(view.Frame, [row], GpuShadows.Offset))
            return null;

        RecordBlockerMips(graph, _blockerMipPasses, atlas, minMips.Value, maxMips.Value, mipSize);

        graph
            .Compute(
                "Resolve sun shadow",
                _resolveShader,
                new GpuConstants(view.Frame.Id, constants.Value.Id),
                RenderStages.Light
            )
            .Reads(
                view.Frame,
                constants.Value,
                input.Cascades.Views,
                atlas,
                minMips.Value,
                maxMips.Value,
                view.Depth,
                view.GBufferNormal,
                view.GBufferSurface
            )
            .Writes(mask.Value, details.Value)
            .DispatchOver(view.Width, view.Height);

        return new ShadowMask(mask.Value, GpuTexture.None, atlas, details.Value);
    }

    /// <summary>
    /// Fits the local lights' tiles for this frame, uploads their rows, copies each tile that the cache holds into the
    /// local atlas, and clears each tile of the cache that it draws again.
    /// </summary>
    /// <returns>
    /// The tiles, or none while shadow maps are off, the copy's shaders build, or what they use is not made.
    /// </returns>
    public LocalShadowViews? Render(in RenderFrame frame, RenderGraph graph, in LocalFitInput input)
    {
        if (!_settings.IsShadowMapsOn)
            return null;

        GpuState state = _gpu.GetState(_copyShaders.Vertex, _copyShaders.Pixel);
        if (state == GpuState.Failed)
            frame.Fail(
                RenderFailure.Shader,
                "Local shadows are not drawn",
                "a shader of the shadow cache was not built."
            );

        if (state != GpuState.Ready)
            return null;

        GpuWorld world = input.World;
        _localTiles.Fit(world, input.Instances.Changed.Span, _settings);
        int size = _localTiles.AtlasSize;
        TextureDescription depth = new(
            "Local shadow atlas",
            size,
            size,
            GpuFormat.D32Sfloat,
            TextureUsages.DepthTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> atlas = _gpu.GetTexture(depth);
        Result<GpuTexture> cache = _gpu.GetTexture(depth with { Name = "Shadow cache" });
        Result<GpuBuffer> cacheViews = _gpu.GetBuffer(
            new("Shadow cache views", (long)LocalTiles.MaxTiles * GpuShadowView.Bytes)
        );
        Result<GpuBuffer> atlasViews = _gpu.GetBuffer(
            new("Local shadow views", (long)LocalTiles.MaxTiles * GpuShadowView.Bytes)
        );
        Result<GpuBuffer> lights = _gpu.GetBuffer(
            new("Local light shadows", (long)_localTiles.Lights.Length * GpuLightShadow.Bytes)
        );
        Result<GpuBuffer> tiles = _gpu.GetBuffer(
            new("Local shadow tiles", (long)LocalTiles.MaxTiles * GpuShadowTile.Bytes)
        );
        Result<GpuBuffer> indices = _gpu.GetBuffer(new("Shadow tile indices", 3 * sizeof(uint), BufferUsages.Index));
        Result asked = Result.FindFailure(atlas, cache, cacheViews, atlasViews, lights, tiles, indices);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Local shadows are not drawn", asked.Error);

            return null;
        }

        // An empty table still uploads one row, so a buffer that the shaders read is never left unwritten.
        bool isStaged =
            graph.Upload<uint>(indices.Value, [0, 1, 2])
            && graph.Upload(cacheViews.Value, _localTiles.CacheViews.AsSpan(0, _localTiles.CacheCount))
            && graph.Upload(atlasViews.Value, _localTiles.AtlasViews.AsSpan(0, _localTiles.AtlasCount))
            && graph.Upload(lights.Value, _localTiles.Lights.AsSpan(0, Math.Max(_localTiles.LightCount, 1)))
            && graph.Upload(tiles.Value, _localTiles.Tiles.AsSpan(0, Math.Max(_localTiles.TileCount, 1)));
        if (!isStaged)
            return null;

        // The atlas is cleared, then each cached tile is copied in; the cache's tiles that draw again are cleared.
        Span<IndexedDraw> draws = _tileDraws;
        GpuConstants copy = new(cache.Value.Id, 0, 0, 0);
        int copies = FillDraws(_localTiles.Copies.AsSpan(0, _localTiles.CopyCount), copy, draws);
        graph
            .Draw(
                "Copy shadow cache",
                _copyShaders,
                GetDepthTarget(atlas.Value, TargetLoad.Clear),
                copy,
                RenderStages.Geometry
            )
            .Reads(cache.Value)
            .DrawIndexed(indices.Value, draws[..copies]);
        if (_localTiles.ClearCount > 0)
        {
            GpuConstants clear = new(0, 0, 0, 0);
            int clears = FillDraws(_localTiles.Clears.AsSpan(0, _localTiles.ClearCount), clear, draws);
            graph
                .Draw(
                    "Clear shadow cache tiles",
                    _copyShaders,
                    GetDepthTarget(cache.Value, TargetLoad.Keep),
                    clear,
                    RenderStages.Geometry
                )
                .DrawIndexed(indices.Value, draws[..clears]);
        }

        return new LocalShadowViews
        {
            CacheViews = cacheViews.Value,
            CacheCount = _localTiles.CacheCount,
            AtlasViews = atlasViews.Value,
            AtlasCount = _localTiles.AtlasCount,
            Cache = cache.Value,
            Atlas = atlas.Value,
            AtlasSize = size,
            Lights = lights.Value,
            Tiles = tiles.Value,
            Anchor = world.Eye.Position,
        };

        static GpuRenderTargets GetDepthTarget(GpuTexture texture, TargetLoad load) =>
            new()
            {
                Depth = new GpuTarget { Texture = texture, Load = load },
            };

        static int FillDraws(ReadOnlySpan<Rectangle> rects, GpuConstants constants, Span<IndexedDraw> draws)
        {
            for (int i = 0; i < rects.Length; i++)
            {
                draws[i] = new IndexedDraw
                {
                    Scissor = rects[i],
                    Constants = constants,
                    IndexCount = 3,
                };
            }

            return rects.Length;
        }
    }

    /// <summary>
    /// Builds the min and max mips of the local atlas once its tiles' depth is drawn, for the blocker search.
    /// </summary>
    /// <returns>
    /// What shading reads to shadow the local lights, or none while the shader builds or the mips are not made.
    /// </returns>
    public LocalShadows? Render(in RenderFrame frame, RenderGraph graph, in LocalResolveInput input)
    {
        GpuState state = _gpu.GetState(_blockerMipShader);
        if (state != GpuState.Ready)
            return null;

        GpuTexture atlas = input.Depth.Atlas;
        int mipSize = Math.Max(input.Views.AtlasSize / 2, 1);
        Result<GpuTexture> minMips = _gpu.GetTexture(
            new("Local min mips", mipSize, mipSize, GpuFormat.R32Sfloat, Levels: BlockerMipLevels)
        );
        Result<GpuTexture> maxMips = _gpu.GetTexture(
            new("Local max mips", mipSize, mipSize, GpuFormat.R32Sfloat, Levels: BlockerMipLevels)
        );
        Result<GpuSampler> sampler = _gpu.GetSampler(
            new SamplerDescription { Filter = TextureFilter.Nearest, Wrap = TextureWrap.Clamp }
        );
        Result asked = Result.FindFailure(minMips, maxMips, sampler);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Local shadows are not resolved", asked.Error);

            return null;
        }

        RecordBlockerMips(graph, _localMipPasses, atlas, minMips.Value, maxMips.Value, mipSize);

        return new LocalShadows
        {
            Lights = input.Views.Lights,
            Tiles = input.Views.Tiles,
            Atlas = atlas,
            MinMips = minMips.Value,
            MaxMips = maxMips.Value,
            MipLevels = BlockerMipLevels,
            Sampler = sampler.Value,
            Anchor = input.Views.Anchor,
            BlockerTaps = Math.Clamp(_settings.BlockerTaps, 1, 64),
            FilterTaps = Math.Clamp(_settings.FilterTaps, 1, 64),
            MaxPenumbraTexels = Math.Clamp(_settings.MaxPenumbraTexels, 1, ShadowMask.WidestPenumbra),
            NormalBias = _settings.NormalBias,
        };
    }

    /// <summary>
    /// Fits the sun's reflective shadow map to the view of this turn: a square from the sun around the camera, snapped
    /// to whole texels so the bounce light keeps still as the camera moves, and uploads its row.
    /// </summary>
    /// <returns>
    /// The map's view and targets, or none while the bounce light or the shadow maps are off, or what it uses is not
    /// made.
    /// </returns>
    public BounceViews? Render(in RenderFrame frame, RenderGraph graph, in BounceFitInput input)
    {
        if (!_settings.IsShadowMapsOn || !_settings.IsBounceOn)
            return null;

        int resolution = Math.Max(_settings.BounceResolution, 1);
        float radius = Math.Max(_settings.BounceRadius, 1);
        Result<GpuBuffer> views = _gpu.GetBuffer(new("Bounce view", GpuShadowView.Bytes), frame.View);
        Result<GpuTexture> depth = _gpu.GetTexture(
            new(
                "Bounce depth",
                resolution,
                resolution,
                GpuFormat.D32Sfloat,
                TextureUsages.DepthTarget | TextureUsages.Sampled
            ),
            frame.View
        );
        TextureDescription color = new(
            "Bounce albedo",
            resolution,
            resolution,
            GpuFormat.R8G8B8A8Unorm,
            TextureUsages.ColorTarget | TextureUsages.Sampled
        );
        Result<GpuTexture> albedo = _gpu.GetTexture(color, frame.View);
        Result<GpuTexture> normal = _gpu.GetTexture(color with { Name = "Bounce normal" }, frame.View);
        Result asked = Result.FindFailure(views, depth, albedo, normal);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Bounce light is not drawn", asked.Error);

            return null;
        }

        FrameConstants camera = input.View.Constants;
        Vector3 cameraPosition = new(camera.CameraPosition.X, camera.CameraPosition.Y, camera.CameraPosition.Z);
        Vector3 towards = input.View.SunDirection;
        float texelSize = 2 * radius / resolution;
        Vector3 center = SnapToTexels(towards, cameraPosition, Vector3.Zero, texelSize);
        GpuShadowView row = CreateSunRow(towards, center, radius, texelSize) with
        {
            RectWidth = (uint)resolution,
            RectHeight = (uint)resolution,
            AtlasTransform = new Vector4(1, 1, 0, 0),
        };
        if (!graph.Upload(views.Value, [row]) || !Matrix4x4.Invert(row.ViewProjection, out Matrix4x4 clipToRelative))
            return null;

        return new BounceViews(views.Value, depth.Value, albedo.Value, normal.Value, clipToRelative);
    }

    /// <summary>
    /// Gathers the sun's light that one bounce brings to each pixel of the view of this turn from its reflective
    /// shadow map, at half size, and blurs it along the surfaces.
    /// </summary>
    /// <returns>
    /// The bounce light, or none while the shaders build or what they write is not made.
    /// </returns>
    public BounceLight? Render(in RenderFrame frame, RenderGraph graph, in BounceInput input)
    {
        ShaderEntry gatherShader = _gatherShader with { TypeArguments = SkyTables.GetSkyType(input.Sky, _openSky) };
        GpuState state = _gpu.GetState(gatherShader, _bounceBlurShader);
        if (state == GpuState.Failed)
            frame.Fail(
                RenderFailure.Shader,
                "Bounce light is not drawn",
                "a shader of the bounce light was not built."
            );

        if (state != GpuState.Ready)
            return null;

        GpuView view = input.View;
        int halfWidth = Math.Max((view.Width + 1) / 2, 1);
        int halfHeight = Math.Max((view.Height + 1) / 2, 1);
        TextureDescription half = new("Gathered bounce", halfWidth, halfHeight, GpuFormat.R16G16B16A16Sfloat);
        Result<GpuTexture> gathered = _gpu.GetTexture(half, frame.View);
        Result<GpuTexture> blurred = _gpu.GetTexture(half with { Name = "Bounce light" }, frame.View);
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Bounce constants", Unsafe.SizeOf<BounceConstants>()),
            frame.View
        );
        Result asked = Result.FindFailure(gathered, blurred, constants);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Bounce light is not drawn", asked.Error);

            return null;
        }

        BounceConstants row = new()
        {
            View = input.Views.Views.Id,
            Depth = input.Maps.Depth.Id,
            Albedo = input.Maps.Albedo.Id,
            Normal = input.Maps.Normal.Id,
            Gathered = gathered.Value.Id,
            Blurred = blurred.Value.Id,
            HalfWidth = (uint)halfWidth,
            HalfHeight = (uint)halfHeight,
            Samples = (uint)Math.Clamp(_settings.BounceSamples, 1, 64),
            Reach = Math.Max(_settings.BounceReach, 0.1f),
            ClipToRelative = input.Views.ClipToRelative,
        };
        if (!graph.Upload(constants.Value, [row]))
            return null;

        GpuConstants header = new(view.Frame.Id, constants.Value.Id);
        ReadOnlySpan<GpuResource> tables = input.Sky is { HasTables: true } made ? [made.Transmittance, made.View] : [];
        graph
            .Compute("Gather bounce light", gatherShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, input.Views.Views, view.Depth, view.GBufferNormal)
            .Reads(input.Maps.Depth, input.Maps.Albedo, input.Maps.Normal)
            .Reads(tables)
            .Writes(gathered.Value)
            .DispatchOver(halfWidth, halfHeight);
        graph
            .Compute("Blur bounce light", _bounceBlurShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, gathered.Value, view.Depth, view.GBufferNormal)
            .Writes(blurred.Value)
            .DispatchOver(halfWidth, halfHeight);

        return new BounceLight(blurred.Value);
    }

    /// <summary>
    /// The depth of split <paramref name="index"/> of <paramref name="count"/> from the near to the far end: the mix
    /// of the logarithmic and the even split by <paramref name="lambda"/>.
    /// </summary>
    private static float GetSplit(int index, int count, float near, float far, float lambda)
    {
        float part = (float)index / count;
        float logarithmic = near * MathF.Pow(far / near, part);
        float even = near + ((far - near) * part);

        return (lambda * logarithmic) + ((1 - lambda) * even);
    }

    private static double Dot(Vector3 a, Vector3 b) => ((double)a.X * b.X) + ((double)a.Y * b.Y) + ((double)a.Z * b.Z);

    /// <summary>
    /// The plane scaled so its normal is a unit.
    /// </summary>
    private static Vector4 ToPlane(Vector4 plane) => plane / new Vector3(plane.X, plane.Y, plane.Z).Length();

    /// <summary>
    /// The sun's view axes: it looks along the light, with the frame's sides as the camera's are, so a triangle keeps
    /// its winding.
    /// </summary>
    private static (Vector3 Right, Vector3 Up, Vector3 Forward) GetSunAxes(Vector3 towards)
    {
        Vector3 forward = -towards;
        Vector3 up0 = MathF.Abs(forward.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up0, forward));

        return (right, Vector3.Cross(forward, right), forward);
    }

    /// <summary>
    /// The camera-relative <paramref name="center"/> moved to whole texels across the sun's view, from the world's
    /// origin, in doubles so a camera far from the origin keeps the snap.
    /// </summary>
    private static Vector3 SnapToTexels(Vector3 towards, Vector3 cameraPosition, Vector3 center, float texelSize)
    {
        (Vector3 right, Vector3 up, _) = GetSunAxes(towards);
        double centerX = Dot(cameraPosition, right) + Vector3.Dot(center, right);
        double centerY = Dot(cameraPosition, up) + Vector3.Dot(center, up);
        double snappedX = Math.Floor(centerX / texelSize) * texelSize;
        double snappedY = Math.Floor(centerY / texelSize) * texelSize;

        return center + (right * (float)(snappedX - centerX)) + (up * (float)(snappedY - centerY));
    }

    /// <summary>
    /// The row of an orthographic view from the sun of the square of half side <paramref name="radius"/> around the
    /// camera-relative <paramref name="center"/>, without its rectangle in an atlas.
    /// </summary>
    private static GpuShadowView CreateSunRow(Vector3 towards, Vector3 center, float radius, float texelSize)
    {
        (Vector3 right, Vector3 up, Vector3 forward) = GetSunAxes(towards);

        // Camera-relative to clip space: across and up over the radius, and reversed depth from the near plane towards
        // the sun to the far side of the sphere.
        float depthRange = CasterReach + radius;
        Matrix4x4 viewProjection = new(
            right.X / radius,
            up.X / radius,
            -forward.X / depthRange,
            0,
            right.Y / radius,
            up.Y / radius,
            -forward.Y / depthRange,
            0,
            right.Z / radius,
            up.Z / radius,
            -forward.Z / depthRange,
            0,
            -Vector3.Dot(center, right) / radius,
            -Vector3.Dot(center, up) / radius,
            (radius + Vector3.Dot(center, forward)) / depthRange,
            1
        );

        // The planes from the columns, for row vectors. The near plane never rejects: a caster nearer the sun is drawn
        // at it.
        Vector4 c0 = new(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        Vector4 c1 = new(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        Vector4 c2 = new(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        Vector4 c3 = new(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        return new GpuShadowView
        {
            ViewProjection = viewProjection,
            Plane0 = ToPlane(c3 + c0),
            Plane1 = ToPlane(c3 - c0),
            Plane2 = ToPlane(c3 + c1),
            Plane3 = ToPlane(c3 - c1),
            Plane4 = new Vector4(0, 0, 0, 1),
            Plane5 = ToPlane(c2),
            Towards = towards,
            TexelSize = texelSize,
            IsOrthographic = 1,
            DepthRange = depthRange,
        };
    }

    /// <summary>
    /// Records the min and max mips of a depth atlas, a level a pass, for the blocker search where it reads wide.
    /// </summary>
    private void RecordBlockerMips(
        RenderGraph graph,
        ImmutableArray<string> names,
        GpuTexture atlas,
        GpuTexture minMips,
        GpuTexture maxMips,
        int mipSize
    )
    {
        for (int level = 0; level < BlockerMipLevels; level++)
        {
            ComputePass build = graph.Compute(
                names[level],
                _blockerMipShader,
                new GpuConstants(atlas.Id, (uint)level, minMips.Id, maxMips.Id),
                RenderStages.Geometry
            );
            if (level == 0)
                build.Reads(atlas);
            else
                build.Reads(minMips, level - 1).Reads(maxMips, level - 1);

            build
                .Writes(minMips, level)
                .Writes(maxMips, level)
                .DispatchOver(Math.Max(mipSize >> level, 1), Math.Max(mipSize >> level, 1));
        }
    }

    /// <summary>
    /// Writes a row into <paramref name="rows"/> for each cascade of <paramref name="view"/>, nearest first, each in its
    /// square of the atlas of two by two of <paramref name="resolution"/> texels.
    /// </summary>
    /// <remarks>
    /// The splits mix even and logarithmic ones by the settings' lambda (Zhang and others, "Parallel-Split Shadow Maps on
    /// Programmable GPUs", GPU Gems 3, ch. 10.2.1). Each cascade is the square around the sphere that holds its slice
    /// of the view, so its size does not change as the camera turns (Valient, "Stable Rendering of Cascaded Shadow
    /// Maps", ShaderX6, 2008), and its centre moves in whole texels across the sun's view.
    /// </remarks>
    private void FitCascades(in GpuView view, int resolution, Span<GpuShadowView> rows)
    {
        FrameConstants camera = view.Constants;
        float near = camera.Near;
        float far = Math.Max(_settings.MaxShadowDistance, near * 2);
        float lambda = Math.Clamp(_settings.SplitLambda, 0, 1);

        // How far the view's corners spread at a metre's depth, squared: the projection's x and y focal lengths.
        float spread =
            (1 / (camera.Projection.M11 * camera.Projection.M11))
            + (1 / (camera.Projection.M22 * camera.Projection.M22));
        Matrix4x4.Invert(camera.View, out Matrix4x4 viewToWorld);
        Vector3 cameraForward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, viewToWorld));
        Vector3 cameraPosition = new(camera.CameraPosition.X, camera.CameraPosition.Y, camera.CameraPosition.Z);

        Vector3 towards = view.SunDirection;
        int atlas = resolution * 2;
        for (int i = 0; i < rows.Length; i++)
        {
            float start = GetSplit(i, rows.Length, near, far, lambda);
            float end = GetSplit(i + 1, rows.Length, near, far, lambda);

            // The smallest sphere around a slice of the view lies on its axis, as far from its near corners as from its
            // far ones, or at its far end's centre when that is further out.
            float along = Math.Min((end + start) * (1 + spread) / 2, end);
            float nearReach = ((along - start) * (along - start)) + (start * start * spread);
            float farReach = ((end - along) * (end - along)) + (end * end * spread);
            float radius = MathF.Ceiling(MathF.Sqrt(Math.Max(nearReach, farReach)) / RadiusStep) * RadiusStep;
            float texelSize = 2 * radius / resolution;

            Vector3 snapped = SnapToTexels(towards, cameraPosition, cameraForward * along, texelSize);

            uint x = (uint)(i % 2 * resolution);
            uint y = (uint)(i / 2 * resolution);
            rows[i] = CreateSunRow(towards, snapped, radius, texelSize) with
            {
                RectX = x,
                RectY = y,
                RectWidth = (uint)resolution,
                RectHeight = (uint)resolution,
                AtlasTransform = new Vector4(
                    (float)resolution / atlas,
                    (float)resolution / atlas,
                    (((2f * x) + resolution) / atlas) - 1,
                    1 - (((2f * y) + resolution) / atlas)
                ),
            };
        }
    }
}

/// <summary>
/// What the sun's resolve reads beyond the frame, 64 bytes, the twin of <c>ResolveSunShadow.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct SunShadowConstants
{
    public uint Views;

    public uint ViewCount;

    public uint Atlas;

    public uint Sampler;

    public uint MinMips;

    public uint MaxMips;

    public uint MipLevels;

    public uint Mask;

    public uint Details;

    public float NormalBias;

    public float CascadeBlend;

    public uint BlockerTaps;

    public uint FilterTaps;

    public float MaxPenumbra;

    public float ContactLength;

    /// <summary>
    /// The steps of the contact shadow's march, 0 for none.
    /// </summary>
    public uint ContactSteps;

    public float ContactThickness;
}

/// <summary>
/// What the bounce light's passes read beyond the frame, 112 bytes, the twin of <c>GatherBounce.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct BounceConstants
{
    public uint View;

    public uint Depth;

    public uint Albedo;

    public uint Normal;

    public uint Gathered;

    public uint Blurred;

    public uint HalfWidth;

    public uint HalfHeight;

    public uint Samples;

    public float Reach;

    public float Padding0;

    public float Padding1;

    public Matrix4x4 ClipToRelative;
}
