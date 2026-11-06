using System.Numerics;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The view's surfaces lit into the HDR picture, and the sky drawn where nothing was: the lighting constants, naming
/// the sky's tables, the lights and the shadow mask, which it fills before the view is drawn; then the sky's tables
/// made again while there is a sky, each tile's lights, each pixel's shadows on a GPU that traces rays, and the
/// shading.
/// </summary>
internal sealed class Lighting(
    IGraphicsApi gpu,
    GpuPipelines pipelines,
    SkyLuts sky,
    LightTiles lights,
    ShadowMask? shadows
) : IDisposable
{
    private readonly GpuPass _shadePass = pipelines.Compute(
        "Shade surfaces",
        "ShadeSurfaces",
        "ShadeSurfaces",
        PassShaders.Atmosphere
    );

    private GpuBuffer _constants;
    private GpuSampler _sampler;
    private bool _hasSky;

    public void Dispose()
    {
        gpu.Destroy(_constants);
        gpu.Destroy(_sampler);
    }

    public Result Create()
    {
        Result<GpuBuffer> constants = gpu.CreateBuffer("Lighting constants", LightingConstants.Bytes);
        if (!constants.Ok)
            return Result.Failure(constants.Error);

        _constants = constants.Value;
        Result<GpuSampler> sampler = gpu.CreateSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (!sampler.Ok)
            return Result.Failure($"the lighting sampler was not made: {sampler.Error}");

        _sampler = sampler.Value;

        return Result.Success();
    }

    /// <summary>
    /// The frame's constants and shadow denoiser's constants uploaded, in a transfer pass the caller opened, after the
    /// lights' table.
    /// </summary>
    public void Prepare(
        RenderGraph graph,
        EcsSnapshot<Sky> skies,
        RenderCamera camera,
        RenderSun sun,
        FrameTargets targets
    )
    {
        _hasSky = skies.Count > 0;
        shadows?.Prepare(graph);
        LightingConstants constants = new()
        {
            SunIlluminance = new Vector4(sun.Radiance, 0),
            CameraAltitude = new Vector4(Math.Max(camera.Transform.Position.Y, 0), 0, 0, 0),
            TransmittanceLut = sky.Transmittance.Id,
            MultiScatteringLut = sky.MultiScattering.Id,
            SkyViewLut = sky.View.Id,
            LutSampler = _sampler.Id,
            HasSky = _hasSky ? 1u : 0u,
            Hdr = targets.Hdr.Id,
            Lights = lights.Table.Id,
            LightCount = (uint)lights.Count,
            LightTiles = lights.Tiles.Id,
            ShadowMask = shadows?.Mask.Id ?? 0,
            TracedShadows = shadows?.Traced.Id ?? 0,
            SunTiles = shadows?.SunTiles.Id ?? 0,
        };
        graph.Upload(gpu, _constants, in constants);
    }

    /// <summary>
    /// The view lit: the sky's tables while there is a sky, each tile's lights from the view's depth, each pixel's
    /// shadows on a GPU that traces rays, then every pixel's surface shaded, and the sky where there is none.
    /// </summary>
    public void Record(in RenderView view)
    {
        FrameTargets targets = view.Targets;
        GpuConstants header = new(view.Constants.Id, 0, _constants.Id);
        if (_hasSky)
            sky.Record(view.Graph, header, view.Constants, _constants);

        lights.Record(view, header, _constants);
        shadows?.Record(view, header, _constants);

        GpuPassRun shade = _shadePass
            .Run(view.Graph, header)
            .Reads(
                view.Constants,
                _constants,
                sky.Transmittance,
                sky.MultiScattering,
                sky.View,
                lights.Table,
                lights.Tiles,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferBase,
                targets.GBufferSurface
            );
        if (shadows is not null)
            shade.Reads(shadows.Mask);

        shade.Writes(targets.Hdr).DispatchOver(targets.Width, targets.Height);
    }
}
