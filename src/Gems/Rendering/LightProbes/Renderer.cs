using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.LightProbes;

/// <summary>
/// What the sky's light takes for each view of the frame: the view, and the sky's tables where a gem made them.
/// </summary>
public readonly record struct SkyLightInput(GpuView View, SkyTables? Sky);

/// <summary>
/// What the sky's direction maps take once a frame: the world, with its main camera, and the instances that changed.
/// </summary>
public readonly record struct SkyFitInput(GpuWorld World, GpuInstances Instances);

/// <summary>
/// What the probes take for the view: the world, with its main camera, the sky's maps with their depth, and the sky's
/// light, which weighs each direction by how bright the sky is there.
/// </summary>
public readonly record struct SkyProbesInput(GpuWorld World, SkyViews Views, SkyDepth Depth, SkyLight Light);

/// <summary>
/// The probes of the frame: volumes of the ambient cube of how much of the sky each probe sees, 2 texels a probe
/// along x. The far ones are <see cref="Counts"/> probes <see cref="Cell"/> metres apart from <see cref="Origin"/> in the
/// world; the near ones are <see cref="Renderer"/>'s dense grid from <see cref="NearOrigin"/>.
/// </summary>
public readonly record struct SkyProbes(
    GpuTexture Volume,
    Vector3 Origin,
    float Cell,
    (int X, int Y, int Z) Counts,
    GpuTexture NearVolume,
    Vector3 NearOrigin
);

/// <summary>
/// What the sky's visibility takes for each view: the view, the sky's maps with their depth, and the far probes.
/// </summary>
public readonly record struct SkyVisibilityInput(GpuView View, SkyViews Views, SkyDepth Depth, SkyProbes Probes);

/// <summary>
/// Makes the sky's light for each view each frame, from the sky as it is: its irradiance and its prefiltered radiance,
/// into <see cref="SkyLight"/>.
/// </summary>
/// <remarks>
/// <para>
/// One pass projects the sky onto spherical harmonics over 64 by 32 directions (Ramamoorthi and Hanrahan, 2001). Then
/// the sky is drawn into an octahedral map with its mips, and each level of a second map is prefiltered by GGX from
/// it with filtered importance sampling (Karis, 2013; Křivánek and Colbert, 2008). Below the horizon, both see the
/// ground, lit by the sky and the sun.
/// </para>
/// </remarks>
/// <para>
/// Once a frame it also fits the sky's direction maps around the main camera (<see cref="SkyMaps"/>), and clears the
/// rectangles that a geometry gem then draws again.
/// </para>
public sealed class Renderer
    : IRender<SkyLightInput, SkyLight>,
        IRender<SkyFitInput, SkyViews>,
        IRender<SkyProbesInput, SkyProbes>,
        IRender<SkyVisibilityInput, SkyVisibility>
{
    /// <summary>
    /// The levels of the sky map: the last is the roughest, a perceptual roughness of 1.
    /// </summary>
    private const int SkyMapLevels = 6;

    /// <summary>
    /// The importance samples of each texel of the prefilter.
    /// </summary>
    private const int PrefilterSamples = 64;

    /// <summary>
    /// The most directions of a set of maps.
    /// </summary>
    private const int MostDirections = 64;

    /// <summary>
    /// The maps of each set side by side across their atlas: 8 by 4 near maps and 4 by 4 far ones.
    /// </summary>
    private const int NearColumns = 8;

    private const int FarColumns = 4;

    /// <summary>
    /// The metres each set's box is snapped to.
    /// </summary>
    private const float NearSnap = 8;

    private const float FarSnap = 32;

    /// <summary>
    /// The far probes: 128 by 16 by 128, 4 m apart, snapped to the far maps' snap across and to a cell in height.
    /// </summary>
    private const int ProbesAcross = 128;

    private const int ProbesHigh = 16;

    private const float ProbeCell = 4;

    /// <summary>
    /// The near probes: 96 by 32 by 96, half a metre apart, snapped to a cell, about the camera. Each is tested again
    /// each frame against the near maps.
    /// </summary>
    private const int NearProbesAcross = 96;

    private const int NearProbesHigh = 32;

    private const float NearProbeCell = 0.5f;

    /// <summary>
    /// The slabs of the probe volume along z that are tested again in turn, one a frame, while the volume stays.
    /// </summary>
    private const int ProbeSlabs = 8;

    /// <summary>
    /// The metres from the camera inside which the near probes alone decide a pixel's visibility, and past which the
    /// far probes alone do: inside the near probes' half width of 24 m.
    /// </summary>
    private const float NearDistance = 20;

    private const float FarDistance = 23;

    /// <summary>
    /// Metres of depth that still count as the point itself: half a near texel of the default maps.
    /// </summary>
    private const float DepthBias = 0.0625f;

    // A name for each level: GPU timings are kept by name.
    private static readonly ImmutableArray<string> _mipPasses =
    [
        "Build sky map mips 0",
        "Build sky map mips 1",
        "Build sky map mips 2",
        "Build sky map mips 3",
        "Build sky map mips 4",
        "Build sky map mips 5",
    ];

    private static readonly ImmutableArray<string> _prefilterPasses =
    [
        "Prefilter sky map 0",
        "Prefilter sky map 1",
        "Prefilter sky map 2",
        "Prefilter sky map 3",
        "Prefilter sky map 4",
        "Prefilter sky map 5",
    ];

    private readonly LightProbesSettings _settings;

    private readonly IGraphicsApi _gpu;

    private readonly ShaderEntry _projectShader;

    private readonly ShaderEntry _drawShader;

    private readonly ShaderEntry _mipShader;

    private readonly ShaderEntry _prefilterShader;

    private readonly ImmutableArray<ShaderTypeArgument> _openSky;

    private readonly DrawShaders _clearShaders;

    private readonly ShaderEntry _probeShader;

    private readonly ShaderEntry _traceShader;

    private readonly ShaderEntry _blurShader;

    private readonly IndexedDraw[] _clearDraws = new IndexedDraw[MostDirections];

    private SkyMaps? _near;

    private SkyMaps? _far;

    /// <summary>
    /// The first corner of the probes that the volume holds, or none before it is first tested whole.
    /// </summary>
    private Vector3? _probeOrigin;

    private int _probeSlab;

    internal Renderer(LightProbesSettings settings, AssetManager assets, IGraphicsApi gpu)
    {
        _settings = settings;
        _gpu = gpu;
        _projectShader = PassShaders.GetEntry(assets, "SkyLight", "ProjectSkyIrradiance");
        _drawShader = PassShaders.GetEntry(assets, "SkyLight", "DrawSkyMap");
        _mipShader = PassShaders.GetEntry(assets, "SkyLight", "BuildSkyMips");
        _prefilterShader = PassShaders.GetEntry(assets, "SkyLight", "PrefilterSkyMap");
        _openSky = SkyTables.GetOpenSky(assets);
        _probeShader = PassShaders.GetEntry(assets, "SkyVisibility", "TestSkyProbes");
        _traceShader = PassShaders.GetEntry(assets, "SkyVisibility", "TraceSkyVisibility");
        _blurShader = PassShaders.GetEntry(assets, "SkyVisibility", "BlurSkyVisibility");
        _clearShaders = new(
            PassShaders.GetEntry(assets, "ClearDepthRects", "ClearDepthRectVertex", ShaderStage.Vertex),
            PassShaders.GetEntry(assets, "ClearDepthRects", "ClearDepthRectPixel", ShaderStage.Pixel)
        )
        {
            Depth = DepthMode.Always,
            Cull = CullMode.None,
        };
    }

    /// <summary>
    /// Fits the sky's direction maps for this frame, uploads their views, and clears the rectangles that this frame
    /// draws again.
    /// </summary>
    /// <returns>
    /// The maps, or none while the sky's visibility is off, the clear's shaders build, or what they use is not made.
    /// </returns>
    public SkyViews? Render(in RenderFrame frame, RenderGraph graph, in SkyFitInput input)
    {
        if (!_settings.IsSkyVisibilityOn)
            return null;

        GpuState state = _gpu.GetState(_clearShaders.Vertex, _clearShaders.Pixel);
        if (state == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "The sky's maps are not drawn", "a shader of them was not built.");

        if (state != GpuState.Ready)
            return null;

        int mapSize = (int)
            BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(_settings.DirectionMapResolution, 64, 2048));
        int nearCount = Math.Clamp(_settings.NearDirections, 1, MostDirections);
        int farCount = Math.Clamp(_settings.FarDirections, 1, MostDirections);
        float nearRegion = Math.Max(_settings.NearRegion, NearSnap);
        float farRegion = Math.Max(_settings.FarRegion, FarSnap);
        if (_near is null || _near.Count != nearCount || _near.MapSize != mapSize || _near.Region != nearRegion)
            _near = new SkyMaps(nearCount, NearColumns, nearRegion, NearSnap, mapSize);

        if (_far is null || _far.Count != farCount || _far.MapSize != mapSize || _far.Region != farRegion)
            _far = new SkyMaps(farCount, FarColumns, farRegion, FarSnap, mapSize);

        // The change budget split between the sets by their maps, so each is whole in the same number of frames.
        int changes = Math.Max(_settings.ChangeRectsPerFrame, 1);
        int nearChanges = Math.Max(changes * nearCount / (nearCount + farCount), 1);
        int farChanges = Math.Max(changes - nearChanges, 1);
        int scroll = Math.Max(_settings.ScrollMapsPerFrame, 1);
        GpuWorld world = input.World;
        Vector3 eye = world.Eye.Position;
        ReadOnlySpan<ChangedInstance> changed = input.Instances.Changed.Span;
        _near.Fit(eye, eye, changed, scroll, nearChanges);
        _far.Fit(eye, eye, changed, scroll, farChanges);

        TextureUsages usage = TextureUsages.DepthTarget | TextureUsages.Sampled;
        Result<GpuTexture> nearAtlas = _gpu.GetTexture(
            new("Near sky atlas", _near.AtlasWidth, _near.AtlasHeight, GpuFormat.D16Unorm, usage)
        );
        Result<GpuTexture> farAtlas = _gpu.GetTexture(
            new("Far sky atlas", _far.AtlasWidth, _far.AtlasHeight, GpuFormat.D16Unorm, usage)
        );
        long setBytes = (long)MostDirections * GpuShadowView.Bytes;
        Result<GpuBuffer> nearDraws = _gpu.GetBuffer(new("Near sky draws", setBytes));
        Result<GpuBuffer> farDraws = _gpu.GetBuffer(new("Far sky draws", setBytes));
        Result<GpuBuffer> nearMaps = _gpu.GetBuffer(new("Near sky maps", setBytes));
        Result<GpuBuffer> farMaps = _gpu.GetBuffer(new("Far sky maps", setBytes));
        Result<GpuBuffer> indices = _gpu.GetBuffer(new("Sky clear indices", 3 * sizeof(uint), BufferUsages.Index));
        Result asked = Result.FindFailure(nearAtlas, farAtlas, nearDraws, farDraws, nearMaps, farMaps, indices);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "The sky's maps are not drawn", asked.Error);

            return null;
        }

        bool isStaged =
            graph.Upload<uint>(indices.Value, [0, 1, 2])
            && graph.Upload(nearDraws.Value, _near.Draws.AsSpan(0, _near.DrawCount))
            && graph.Upload(farDraws.Value, _far.Draws.AsSpan(0, _far.DrawCount))
            && graph.Upload(nearMaps.Value, _near.Maps.AsSpan(0, nearCount))
            && graph.Upload(farMaps.Value, _far.Maps.AsSpan(0, farCount));
        if (!isStaged)
        {
            // What this frame would have drawn waits for the next: each map is drawn whole again.
            _near.Reset();
            _far.Reset();

            return null;
        }

        Clear(graph, "Clear near sky maps", nearAtlas.Value, indices.Value, _near);
        Clear(graph, "Clear far sky maps", farAtlas.Value, indices.Value, _far);

        return new SkyViews
        {
            NearDraws = nearDraws.Value,
            NearDrawCount = _near.DrawCount,
            FarDraws = farDraws.Value,
            FarDrawCount = _far.DrawCount,
            NearAtlas = nearAtlas.Value,
            FarAtlas = farAtlas.Value,
            NearMaps = nearMaps.Value,
            NearCount = nearCount,
            FarMaps = farMaps.Value,
            FarCount = farCount,
            Anchor = eye,
        };
    }

    /// <summary>
    /// Records the sky's light of the view of this turn.
    /// </summary>
    /// <returns>
    /// The sky's light, or none while its shaders build or what it writes is not made.
    /// </returns>
    public SkyLight? Render(in RenderFrame frame, RenderGraph graph, in SkyLightInput input)
    {
        ImmutableArray<ShaderTypeArgument> sky = SkyTables.GetSkyType(input.Sky, _openSky);
        ShaderEntry projectShader = _projectShader with { TypeArguments = sky };
        ShaderEntry drawShader = _drawShader with { TypeArguments = sky };
        GpuState state = _gpu.GetState(projectShader, drawShader, _mipShader, _prefilterShader);
        if (state == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "The sky's light is not made", "a shader of it was not built.");

        if (state != GpuState.Ready)
            return null;

        GpuView view = input.View;
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(_settings.SkyMapResolution, 16, 1024));
        Result<GpuBuffer> irradiance = _gpu.GetBuffer(new("Sky irradiance", 10 * 16), frame.View);
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Sky light constants", SkyMapLevels * Unsafe.SizeOf<SkyLightConstants>()),
            frame.View
        );
        TextureDescription map = new("Sky map", size, size, GpuFormat.R16G16B16A16Sfloat, Levels: SkyMapLevels);
        Result<GpuTexture> source = _gpu.GetTexture(map with { Name = "Sky map source" }, frame.View);
        Result<GpuTexture> prefiltered = _gpu.GetTexture(map, frame.View);
        Result<GpuSampler> sampler = _gpu.GetSampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        Result asked = Result.FindFailure(irradiance, constants, source, prefiltered, sampler);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "The sky's light is not made", asked.Error);

            return null;
        }

        // A row of constants a level, so each pass reads its own level.
        Span<SkyLightConstants> rows = graph.Upload<SkyLightConstants>(constants.Value, SkyMapLevels);
        if (rows.Length < SkyMapLevels)
            return null;

        for (int level = 0; level < SkyMapLevels; level++)
        {
            rows[level] = new SkyLightConstants
            {
                Irradiance = irradiance.Value.Id,
                Source = source.Value.Id,
                Map = prefiltered.Value.Id,
                Sampler = sampler.Value.Id,
                Size = (uint)size,
                Levels = SkyMapLevels,
                Level = (uint)level,
                Samples = PrefilterSamples,
            };
        }

        ReadOnlySpan<GpuResource> tables = input.Sky is { HasTables: true } made ? [made.Transmittance, made.View] : [];
        GpuConstants header = new(view.Frame.Id, constants.Value.Id);
        graph
            .Compute("Project sky SH", projectShader, header, RenderStages.Light)
            .Reads(tables)
            .Reads(view.Frame, constants.Value)
            .Writes(irradiance.Value)
            .Dispatch(1);
        graph
            .Compute("Draw sky map", drawShader, header, RenderStages.Light)
            .Reads(tables)
            .Reads(view.Frame, constants.Value, irradiance.Value)
            .Writes(source.Value, 0)
            .DispatchOver(size, size);
        for (int level = 1; level < SkyMapLevels; level++)
        {
            int side = Math.Max(size >> level, 1);
            graph
                .Compute(_mipPasses[level], _mipShader, GetHeader(level), RenderStages.Light)
                .Reads(view.Frame, constants.Value)
                .Reads(source.Value, level - 1)
                .Writes(source.Value, level)
                .DispatchOver(side, side);
        }

        for (int level = 0; level < SkyMapLevels; level++)
        {
            int side = Math.Max(size >> level, 1);
            graph
                .Compute(_prefilterPasses[level], _prefilterShader, GetHeader(level), RenderStages.Light)
                .Reads(view.Frame, constants.Value, source.Value)
                .Writes(prefiltered.Value, level)
                .DispatchOver(side, side);
        }

        return new SkyLight(irradiance.Value, prefiltered.Value, SkyMapLevels, sampler.Value);

        GpuConstants GetHeader(int level) =>
            new(view.Frame.Id, constants.Value.Id, (uint)(level * Unsafe.SizeOf<SkyLightConstants>()));
    }

    /// <summary>
    /// Tests the far probes against the far maps and the near probes against the near maps, for the view.
    /// </summary>
    /// <returns>
    /// The probes, or none while the shader builds or what it writes is not made.
    /// </returns>
    public SkyProbes? Render(in RenderFrame frame, RenderGraph graph, in SkyProbesInput input)
    {
        GpuState state = _gpu.GetState(_probeShader);
        if (state != GpuState.Ready)
            return null;

        Vector3 eye = input.World.Eye.Position;
        Vector3 origin = new(
            (MathF.Round(eye.X / FarSnap) * FarSnap) - (ProbesAcross / 2 * ProbeCell),
            (MathF.Round(eye.Y / ProbeCell) * ProbeCell) - (ProbesHigh / 2 * ProbeCell),
            (MathF.Round(eye.Z / FarSnap) * FarSnap) - (ProbesAcross / 2 * ProbeCell)
        );
        Result<GpuTexture> volume = _gpu.GetTexture(
            new("Sky probes", 2 * ProbesAcross, ProbesHigh, GpuFormat.R16G16B16A16Sfloat)
            {
                Kind = TextureKind.Texture3D,
                Depth = ProbesAcross,
            }
        );
        Vector3 nearOrigin = new(
            (MathF.Round(eye.X / NearProbeCell) * NearProbeCell) - (NearProbesAcross / 2 * NearProbeCell),
            (MathF.Round(eye.Y / NearProbeCell) * NearProbeCell) - (NearProbesHigh / 2 * NearProbeCell),
            (MathF.Round(eye.Z / NearProbeCell) * NearProbeCell) - (NearProbesAcross / 2 * NearProbeCell)
        );
        Result<GpuTexture> nearVolume = _gpu.GetTexture(
            new("Near sky probes", 2 * NearProbesAcross, NearProbesHigh, GpuFormat.R16G16B16A16Sfloat)
            {
                Kind = TextureKind.Texture3D,
                Depth = NearProbesAcross,
            }
        );
        int constantsBytes = Unsafe.SizeOf<SkyVisibilityConstants>();
        Result<GpuBuffer> constants = _gpu.GetBuffer(new("Sky probe constants", constantsBytes));
        Result<GpuBuffer> nearConstants = _gpu.GetBuffer(new("Near sky probe constants", constantsBytes));
        Result<GpuSampler> sampler = _gpu.GetSampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        Result asked = Result.FindFailure(volume, nearVolume, constants, nearConstants, sampler);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "The sky's probes are not made", asked.Error);

            return null;
        }

        // A volume that moved, or that is new, is tested whole. Else one slab along z a frame is tested again, so a
        // change of the far maps reaches each probe within ProbeSlabs frames.
        bool isWhole = _probeOrigin != origin;
        int slabDepth = ProbesAcross / ProbeSlabs;
        int firstZ = isWhole ? 0 : _probeSlab * slabDepth;
        int depth = isWhole ? ProbesAcross : slabDepth;
        SkyVisibilityConstants row = CreateConstants(input.Views, sampler.Value) with
        {
            Probes = volume.Value.Id,
            ProbeOrigin = origin,
            ProbeFirstZ = (uint)firstZ,
            NearProbes = nearVolume.Value.Id,
            NearOrigin = nearOrigin,
            SkyIrradiance = input.Light.Irradiance.Id,
        };
        if (!graph.Upload(constants.Value, [row]) || !graph.Upload(nearConstants.Value, [row with { ProbeSet = 1 }]))
            return null;

        graph
            .Compute(
                "Test sky probes",
                _probeShader,
                new GpuConstants(input.World.Frame.Id, constants.Value.Id),
                RenderStages.Light
            )
            .Reads(input.World.Frame, constants.Value, input.Views.FarMaps, input.Depth.FarAtlas)
            .Reads(input.Light.Irradiance)
            .Writes(volume.Value)
            .DispatchOver(ProbesAcross, ProbesHigh, depth);
        graph
            .Compute(
                "Test near sky probes",
                _probeShader,
                new GpuConstants(input.World.Frame.Id, nearConstants.Value.Id),
                RenderStages.Light
            )
            .Reads(input.World.Frame, nearConstants.Value, input.Views.NearMaps, input.Depth.NearAtlas)
            .Reads(input.Light.Irradiance)
            .Writes(nearVolume.Value)
            .DispatchOver(NearProbesAcross, NearProbesHigh, NearProbesAcross);
        _probeOrigin = origin;
        _probeSlab = isWhole ? 0 : (_probeSlab + 1) % ProbeSlabs;

        return new SkyProbes(
            volume.Value,
            origin,
            ProbeCell,
            (ProbesAcross, ProbesHigh, ProbesAcross),
            nearVolume.Value,
            nearOrigin
        );
    }

    /// <summary>
    /// Finds how much of the sky each pixel of the view of this turn sees, at half size, and blurs it.
    /// </summary>
    /// <returns>
    /// The view's sky visibility, or none while its shaders build or what it writes is not made.
    /// </returns>
    public SkyVisibility? Render(in RenderFrame frame, RenderGraph graph, in SkyVisibilityInput input)
    {
        GpuState state = _gpu.GetState(_traceShader, _blurShader);
        if (state == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "The sky's visibility is not found", "a shader of it was not built.");

        if (state != GpuState.Ready)
            return null;

        GpuView view = input.View;
        int halfWidth = Math.Max((view.Width + 1) / 2, 1);
        int halfHeight = Math.Max((view.Height + 1) / 2, 1);
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Sky visibility constants", Unsafe.SizeOf<SkyVisibilityConstants>()),
            frame.View
        );
        Result<GpuTexture> traced = _gpu.GetTexture(
            new("Sky visibility traced", halfWidth, halfHeight, GpuFormat.R16G16Unorm),
            frame.View
        );
        Result<GpuTexture> blurred = _gpu.GetTexture(
            new("Sky visibility", halfWidth, halfHeight, GpuFormat.R16G16Unorm),
            frame.View
        );
        Result<GpuSampler> sampler = _gpu.GetSampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        Result asked = Result.FindFailure(constants, traced, blurred, sampler);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "The sky's visibility is not found", asked.Error);

            return null;
        }

        SkyProbes probes = input.Probes;
        SkyVisibilityConstants row = CreateConstants(input.Views, sampler.Value) with
        {
            Probes = probes.Volume.Id,
            ProbeOrigin = probes.Origin,
            NearProbes = probes.NearVolume.Id,
            NearOrigin = probes.NearOrigin,
            Traced = traced.Value.Id,
            Blurred = blurred.Value.Id,
            HalfWidth = (uint)halfWidth,
            HalfHeight = (uint)halfHeight,
        };
        if (!graph.Upload(constants.Value, [row]))
            return null;

        GpuConstants header = new(view.Frame.Id, constants.Value.Id);
        graph
            .Compute("Trace sky visibility", _traceShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, view.Depth, view.GBufferNormal)
            .Reads(probes.Volume, probes.NearVolume)
            .Writes(traced.Value)
            .DispatchOver(halfWidth, halfHeight);
        graph
            .Compute("Blur sky visibility", _blurShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, view.Depth, view.GBufferNormal, traced.Value)
            .Writes(blurred.Value)
            .DispatchOver(halfWidth, halfHeight);

        return new SkyVisibility(blurred.Value);
    }

    /// <summary>
    /// The constants that the probes and the pixels share: the maps, the probes' size and the distances.
    /// </summary>
    private SkyVisibilityConstants CreateConstants(in SkyViews views, GpuSampler sampler) =>
        new()
        {
            NearMaps = views.NearMaps.Id,
            NearCount = (uint)views.NearCount,
            FarMaps = views.FarMaps.Id,
            FarCount = (uint)views.FarCount,
            NearAtlas = views.NearAtlas.Id,
            FarAtlas = views.FarAtlas.Id,
            Sampler = sampler.Id,
            Anchor = views.Anchor,
            NormalOffset = Math.Max(_settings.SkyNormalOffset, 0),
            ProbeCell = ProbeCell,
            ProbeCountX = ProbesAcross,
            ProbeCountY = ProbesHigh,
            ProbeCountZ = ProbesAcross,
            NearDistance = NearDistance,
            FarDistance = FarDistance,
            DepthBias = DepthBias,
            NearCell = NearProbeCell,
            NearCountX = NearProbesAcross,
            NearCountY = NearProbesHigh,
            NearCountZ = NearProbesAcross,
        };

    /// <summary>
    /// Clears to no depth each rectangle of <paramref name="atlas"/> that <paramref name="maps"/> draw again this frame.
    /// </summary>
    private void Clear(RenderGraph graph, string name, GpuTexture atlas, GpuBuffer indices, SkyMaps maps)
    {
        if (maps.ClearCount == 0)
            return;

        GpuConstants none = default;
        for (int i = 0; i < maps.ClearCount; i++)
        {
            _clearDraws[i] = new IndexedDraw
            {
                Scissor = maps.Clears[i],
                Constants = none,
                IndexCount = 3,
            };
        }

        graph
            .Draw(
                name,
                _clearShaders,
                new GpuRenderTargets
                {
                    Depth = new GpuTarget { Texture = atlas, Load = TargetLoad.Keep },
                },
                none,
                RenderStages.Geometry
            )
            .DrawIndexed(indices, _clearDraws.AsSpan(0, maps.ClearCount));
    }
}

/// <summary>
/// What the sky's passes read beyond the frame, 32 bytes, the twin of <c>SkyLight.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct SkyLightConstants
{
    public uint Irradiance;

    public uint Source;

    public uint Map;

    public uint Sampler;

    public uint Size;

    public uint Levels;

    public uint Level;

    public uint Samples;
}

/// <summary>
/// What the sky's visibility passes read beyond the frame, 160 bytes, the twin of <c>SkyVisibility.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct SkyVisibilityConstants
{
    public uint NearMaps;

    public uint NearCount;

    public uint FarMaps;

    public uint FarCount;

    public uint NearAtlas;

    public uint FarAtlas;

    public uint Sampler;

    public uint Probes;

    public uint Traced;

    public uint Blurred;

    public uint HalfWidth;

    public uint HalfHeight;

    public Vector3 Anchor;

    public float NormalOffset;

    public Vector3 ProbeOrigin;

    public float ProbeCell;

    public uint ProbeCountX;

    public uint ProbeCountY;

    public uint ProbeCountZ;

    public float NearDistance;

    public float FarDistance;

    public float DepthBias;

    public uint ProbeFirstZ;

    public uint ProbeSet;

    public Vector3 NearOrigin;

    public float NearCell;

    public uint NearCountX;

    public uint NearCountY;

    public uint NearCountZ;

    public uint NearProbes;

    public uint SkyIrradiance;

    public uint Padding0;

    public uint Padding1;

    public uint Padding2;
}
