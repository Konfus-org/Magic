using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Ecs.Components;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace GIRendering;

/// <summary>
/// What everything that lights a surface shares: the lighting constants, naming the sky's tables, the lights' bins and
/// the air it holds, which it fills before the view is drawn.
/// </summary>
internal sealed class Lighting(
    IGraphicsApi gpu,
    RenderGraph graph,
    SunShadows shadows,
    DistanceField field,
    SkyLuts sky,
    LightBins bins,
    FogVolume fogVolume
) : IDisposable
{
    private GpuSampler _sampler;
    private bool _hasFog;
    private bool _hasSky;

    public GpuBuffer Constants { get; private set; }

    /// <summary>
    /// A table a lighting pass fills and reads, 3D when it has a depth.
    /// </summary>
    public static Result<GpuTexture> CreateTable(IGraphicsApi gpu, string name, int width, int height, int depth) =>
        gpu.CreateTexture(
            new TextureDescription
            {
                Width = width,
                Height = height,
                Depth = depth,
                Kind = depth > 1 ? TextureKind.Texture3D : TextureKind.Texture2D,
                Format = GpuFormat.R16G16B16A16Sfloat,
                Usage = TextureUsages.Storage | TextureUsages.Sampled,
                Name = name,
            }
        );

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
    /// The frame's lights and constants uploaded and the froxel lists emptied, in a transfer pass the caller opened.
    /// </summary>
    public void Prepare(
        RenderWorld world,
        RenderCamera camera,
        RenderSun sun,
        FrameTargets targets,
        DebugView debugView,
        GpuBuffer giConstants,
        GpuBuffer glassConstants,
        GpuBuffer postConstants
    )
    {
        bins.Prepare(world);
        _hasFog = world.Fog is not null;
        _hasSky = world.HasSky;
        Fog fog = world.Fog ?? new Fog();
        LightingConstants constants = new()
        {
            SunIlluminance = new Vector4(sun.Radiance, 0),
            CameraAltitude = new Vector4(Math.Max(camera.Transform.Position.Y, 0), 0, 0, 0),
            Fog = new FogConstants
            {
                Albedo = new Vector3(fog.Color.R, fog.Color.G, fog.Color.B),
                Density = fog.Density,
                Height = fog.Height,
                HalfHeight = fog.HalfHeight,
                Anisotropy = fog.Anisotropy,
                IsPresent = _hasFog ? 1u : 0u,
            },
            Lights = bins.Table.Id,
            LightCount = (uint)bins.Count,
            FroxelLists = bins.FroxelLists.Id,
            TransmittanceLut = sky.Transmittance.Id,
            MultiScatteringLut = sky.MultiScattering.Id,
            SkyViewLut = sky.View.Id,
            LutSampler = _sampler.Id,
            FogScattering = fogVolume.Scattering.Id,
            FogIntegrated = fogVolume.Integrated.Id,
            FogSampler = _sampler.Id,
            Hdr = targets.Hdr.Id,
            HasSky = _hasSky ? 1u : 0u,
            Exposure = sun.Exposure,
            DebugView = (uint)debugView,
            Gi = giConstants.Id,
            Translucency = glassConstants.Id,
            Post = postConstants.Id,
        };
        GpuUpload upload = gpu.BeginUpload(LightingConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, LightingConstants.Bytes);
    }

    /// <summary>
    /// What a pass that lights surfaces is told of the frame.
    /// </summary>
    public GpuConstants GetHeader(GpuBuffer frameConstants) =>
        new(frameConstants.Id, shadows.Constants.Id, Constants.Id, field.Constants.Id);

    /// <summary>
    /// Says a pass lights surfaces away from the view, as the global illumination's do: the light table and the
    /// sky's tables, without the froxel lists.
    /// </summary>
    public void UseForCards()
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(bins.Table, GpuAccess.ShaderRead);
        sky.UseForReading();
    }

    /// <summary>
    /// Says a pass lights surfaces in the view, as the shading and the glass do: the light table, its froxel lists, the
    /// sky's tables and the air.
    /// </summary>
    public void UseForShading()
    {
        UseForCards();
        graph.Use(bins.FroxelLists, GpuAccess.ShaderRead);
        graph.Use(fogVolume.Integrated, GpuAccess.ShaderRead);
    }

    /// <summary>
    /// The sky's tables and the froxel lists, before the view is drawn: what the fog and the shading read.
    /// </summary>
    public void RecordTables(GpuBuffer frameConstants)
    {
        GpuConstants header = new(frameConstants.Id, shadows.Constants.Id, Constants.Id);
        if (_hasSky)
            sky.Record(header, frameConstants, Constants);

        if (bins.Count > 0)
            bins.Record(header, frameConstants, Constants);
    }

    /// <summary>
    /// The air, after the sun's pages, before the shading reads it.
    /// </summary>
    public void RecordFog(GpuBuffer frameConstants)
    {
        if (_hasFog)
            fogVolume.Record(GetHeader(frameConstants), frameConstants, Constants);
    }
}
