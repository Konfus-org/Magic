using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// What everything that lights a surface shares: the lighting constants, naming the sky's tables, the lights and the
/// shadow mask, which it fills before the view is drawn, the sky's tables made again each frame while there is a sky,
/// each tile's lights, and each pixel's shadows on a GPU that traces rays.
/// </summary>
internal sealed class Lighting(IGraphicsApi gpu, RenderGraph graph, SkyLuts sky, LightTiles lights, ShadowMask? shadows)
    : IDisposable
{
    private GpuSampler _sampler;
    private bool _hasSky;

    public GpuBuffer Constants { get; private set; }

    public void Dispose()
    {
        if (!Constants.IsNone)
            gpu.Destroy(Constants);

        if (!_sampler.IsNone)
            gpu.Destroy(_sampler);
    }

    public Result Create()
    {
        Result<GpuBuffer> constants = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = LightingConstants.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Lighting constants",
            }
        );
        if (!constants.Ok)
            return Result.Failure($"the Lighting constants buffer was not made: {constants.Error}");

        Constants = constants.Value;
        Result<GpuSampler> sampler = gpu.CreateSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, HasMipmapBlend = false }
        );
        if (!sampler.Ok)
            return Result.Failure($"the lighting sampler was not made: {sampler.Error}");

        _sampler = sampler.Value;

        return Result.Success();
    }

    /// <summary>
    /// The frame's constants and lights uploaded, in a transfer pass the caller opened.
    /// </summary>
    public void Prepare(RenderWorld world, RenderCamera camera, RenderSun sun, FrameTargets targets)
    {
        _hasSky = world.HasSky;
        lights.Prepare(world);
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
        };
        GpuUpload upload = gpu.BeginUpload(LightingConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, LightingConstants.Bytes);
    }

    /// <summary>
    /// What a pass that lights surfaces is told of the frame.
    /// </summary>
    public GpuConstants GetHeader(GpuBuffer frameConstants) => new(frameConstants.Id, 0, Constants.Id);

    /// <summary>
    /// The sky's tables, while there is a sky.
    /// </summary>
    public void RecordTables(GpuBuffer frameConstants)
    {
        if (_hasSky)
            sky.Record(GetHeader(frameConstants), frameConstants, Constants);
    }

    /// <summary>
    /// Each tile's lights, from the view's depth.
    /// </summary>
    public void RecordTiles(in RenderView view) => lights.Record(view, GetHeader(view.Constants), Constants);

    /// <summary>
    /// Each pixel's shadows, on a GPU that traces rays.
    /// </summary>
    public void RecordShadows(in RenderView view) => shadows?.Record(view, GetHeader(view.Constants), Constants);

    /// <summary>
    /// Says a pass reads the constants, the sky's tables, the lights and the shadows.
    /// </summary>
    public void UseForShading()
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        sky.UseForReading();
        lights.UseForReading();
        if (shadows is not null)
            graph.Use(shadows.Mask, GpuAccess.ShaderRead);
    }
}
