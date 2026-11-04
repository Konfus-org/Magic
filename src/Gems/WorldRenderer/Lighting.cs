using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Ecs.Components;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// What lights the frame: the light table and the froxel lists the lights are binned into, the sky's three tables
/// made each frame, the fog volumes, and the passes that fill them and light the GBuffer into the HDR picture, with
/// the global illumination's probes.
/// </summary>
internal sealed class Lighting(IGraphicsApi gpu) : IDisposable
{
    public const int MaxLights = 4096;

    private const int LightGridX = 32;
    private const int LightGridY = 18;
    private const int LightGridZ = 48;
    private const int LightsPerFroxel = 128;
    private const int FogGridX = 96;
    private const int FogGridY = 54;
    private const int FogGridZ = 64;
    private const int FroxelCount = LightGridX * LightGridY * LightGridZ;
    private const int BinThreads = 64;
    private const int FogThreads = 4;

    private readonly GpuLight[] _lights = new GpuLight[MaxLights];
    private GpuBuffer _lightTable;
    private GpuBuffer _froxelLists;
    private GpuTexture _transmittance;
    private GpuTexture _multiScattering;
    private GpuTexture _skyView;
    private GpuTexture _fogScattering;
    private GpuTexture _fogIntegrated;
    private GpuSampler _lutSampler;
    private int _lightCount;
    private bool _hasFog;
    private bool _hasSky;

    public GpuBuffer Constants { get; private set; }

    public void Dispose()
    {
        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[Constants, _lightTable, _froxelLists])
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }

        foreach (
            GpuTexture texture in (ReadOnlySpan<GpuTexture>)
                [_transmittance, _multiScattering, _skyView, _fogScattering, _fogIntegrated]
        )
        {
            if (!texture.IsNone)
                gpu.Destroy(texture);
        }

        if (!_lutSampler.IsNone)
            gpu.Destroy(_lutSampler);
    }

    public Result Create()
    {
        (string Name, long Bytes, BufferUsages Usage)[] buffers =
        [
            ("Lighting constants", LightingConstants.Bytes, BufferUsages.Storage),
            ("Light table", (long)MaxLights * GpuLight.Bytes, BufferUsages.Storage),
            ("Froxel light lists", (long)FroxelCount * (LightsPerFroxel + 1) * 4, BufferUsages.Storage),
        ];
        GpuBuffer[] made = new GpuBuffer[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            Result<GpuBuffer> buffer = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = buffers[i].Bytes,
                    Usage = buffers[i].Usage,
                    Name = buffers[i].Name,
                }
            );
            if (!buffer.Ok)
                return Result.Failure($"the {buffers[i].Name} buffer was not made: {buffer.Error}");

            made[i] = buffer.Value;
        }

        (Constants, _lightTable, _froxelLists) = (made[0], made[1], made[2]);
        Result<GpuTexture> transmittance = MakeLut("Sky transmittance", 256, 64, 1);
        Result<GpuTexture> multiScattering = MakeLut("Sky multi-scattering", 32, 32, 1);
        Result<GpuTexture> skyView = MakeLut("Sky view", 192, 108, 1);
        Result<GpuTexture> fogScattering = MakeLut("Fog scattering", FogGridX, FogGridY, FogGridZ);
        Result<GpuTexture> fogIntegrated = MakeLut("Fog integrated", FogGridX, FogGridY, FogGridZ);
        foreach (
            Result<GpuTexture> texture in (ReadOnlySpan<Result<GpuTexture>>)
                [transmittance, multiScattering, skyView, fogScattering, fogIntegrated]
        )
        {
            if (!texture.Ok)
                return Result.Failure($"a lighting table was not made: {texture.Error}");
        }

        (_transmittance, _multiScattering, _skyView, _fogScattering, _fogIntegrated) = (
            transmittance.Value,
            multiScattering.Value,
            skyView.Value,
            fogScattering.Value,
            fogIntegrated.Value
        );
        Result<GpuSampler> sampler = gpu.CreateSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, HasMipmapBlend = false }
        );
        if (!sampler.Ok)
            return Result.Failure($"the lighting sampler was not made: {sampler.Error}");

        _lutSampler = sampler.Value;

        return Result.Success();
    }

    /// <summary>
    /// The frame's lights and constants uploaded and the froxel lists emptied, in a transfer pass the caller opened.
    /// </summary>
    public void Prepare(
        RenderGraph graph,
        RenderWorld world,
        RenderCamera camera,
        RenderSun sun,
        FrameTargets targets,
        DebugView debugView,
        GpuBuffer giConstants,
        GpuBuffer translucencyConstants,
        GpuBuffer postConstants
    )
    {
        _lightCount = Math.Min(world.Lights.Count, MaxLights);
        world.Lights.CopyTo(0, _lights, 0, _lightCount);
        _hasFog = world.Fog is not null;
        _hasSky = world.HasSky;
        if (_lightCount > 0)
        {
            int bytes = _lightCount * GpuLight.Bytes;
            GpuUpload lights = gpu.BeginUpload(bytes);
            MemoryMarshal.AsBytes(_lights.AsSpan(0, _lightCount)).CopyTo(lights.Bytes);
            graph.Use(_lightTable, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(lights.Buffer, lights.Offset, _lightTable, 0, (ulong)bytes);
        }

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
            Lights = _lightTable.Id,
            LightCount = (uint)_lightCount,
            FroxelLists = _froxelLists.Id,
            TransmittanceLut = _transmittance.Id,
            MultiScatteringLut = _multiScattering.Id,
            SkyViewLut = _skyView.Id,
            LutSampler = _lutSampler.Id,
            FogScattering = _fogScattering.Id,
            FogIntegrated = _fogIntegrated.Id,
            FogSampler = _lutSampler.Id,
            Hdr = targets.Hdr.Id,
            HasSky = _hasSky ? 1u : 0u,
            Exposure = sun.Exposure,
            DebugView = (uint)debugView,
            Gi = giConstants.Id,
            Translucency = translucencyConstants.Id,
            Post = postConstants.Id,
        };
        GpuUpload upload = gpu.BeginUpload(LightingConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, LightingConstants.Bytes);
        graph.Use(_froxelLists, GpuAccess.CopyDestination);
        graph.Recorder.FillBuffer(_froxelLists, 0);
    }

    /// <summary>
    /// Says a pass lights surfaces away from the view, as the global illumination's do: the light table and the
    /// sky's tables, without the froxel lists.
    /// </summary>
    public void UseForCards(RenderGraph graph)
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_lightTable, GpuAccess.ShaderRead);
        graph.Use(_transmittance, GpuAccess.ShaderRead);
        graph.Use(_multiScattering, GpuAccess.ShaderRead);
        graph.Use(_skyView, GpuAccess.ShaderRead);
    }

    /// <summary>
    /// Says a pass lights surfaces in the view, as the translucent one does: the light table, its froxel lists, the
    /// sky's tables and the air.
    /// </summary>
    public void UseForShading(RenderGraph graph)
    {
        UseForCards(graph);
        graph.Use(_froxelLists, GpuAccess.ShaderRead);
        graph.Use(_fogIntegrated, GpuAccess.ShaderRead);
    }

    /// <summary>
    /// The sky's tables and the froxel lists, before the view is drawn: what the fog and the lighting read.
    /// </summary>
    public void RecordTables(
        RenderGraph graph,
        RendererPipelines pipelines,
        GpuBuffer frameConstants,
        SunShadows shadows
    )
    {
        GpuConstants header = new(frameConstants.Id, shadows.Constants.Id, Constants.Id);
        if (_hasSky)
        {
            graph.BeginComputePass("Sky: transmittance");
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(_transmittance, GpuAccess.ShaderWrite);
            graph.Recorder.BindPipeline(pipelines.SkyTransmittance);
            graph.Recorder.PushConstants(header);
            graph.Recorder.Dispatch(256 / 8, 64 / 8);
            graph.EndPass();

            graph.BeginComputePass("Sky: multi-scattering");
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(_transmittance, GpuAccess.ShaderRead);
            graph.Use(_multiScattering, GpuAccess.ShaderWrite);
            graph.Recorder.BindPipeline(pipelines.SkyMultiScattering);
            graph.Recorder.PushConstants(header);
            graph.Recorder.Dispatch(32 / 8, 32 / 8);
            graph.EndPass();

            graph.BeginComputePass("Sky: view");
            graph.Use(frameConstants, GpuAccess.ShaderRead);
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(_transmittance, GpuAccess.ShaderRead);
            graph.Use(_multiScattering, GpuAccess.ShaderRead);
            graph.Use(_skyView, GpuAccess.ShaderWrite);
            graph.Recorder.BindPipeline(pipelines.SkyView);
            graph.Recorder.PushConstants(header);
            graph.Recorder.Dispatch(192 / 8, (108 + 7) / 8);
            graph.EndPass();
        }

        if (_lightCount > 0)
        {
            graph.BeginComputePass("Bin lights");
            graph.Use(frameConstants, GpuAccess.ShaderRead);
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(_lightTable, GpuAccess.ShaderRead);
            graph.Use(_froxelLists, GpuAccess.ShaderWrite);
            graph.Recorder.BindPipeline(pipelines.BinLights);
            graph.Recorder.PushConstants(header);
            graph.Recorder.Dispatch((uint)((_lightCount + BinThreads - 1) / BinThreads));
            graph.EndPass();
        }
    }

    /// <summary>
    /// The air, then the GBuffer lit into the HDR picture, after the sun's pages.
    /// </summary>
    public void RecordShading(
        RenderGraph graph,
        RendererPipelines pipelines,
        GpuBuffer frameConstants,
        SunShadows shadows,
        DistanceField field,
        GlobalIllumination gi,
        Translucency translucency,
        CardTable cards,
        GpuBuffer instances,
        GpuBuffer visibleClusters,
        FrameTargets targets,
        bool usesRayQuery
    )
    {
        GpuConstants header = new(frameConstants.Id, shadows.Constants.Id, Constants.Id, field.Constants.Id);
        if (_hasFog)
        {
            graph.BeginComputePass("Fog: inject");
            graph.Use(frameConstants, GpuAccess.ShaderRead);
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(_lightTable, GpuAccess.ShaderRead);
            graph.Use(_froxelLists, GpuAccess.ShaderRead);
            graph.Use(_transmittance, GpuAccess.ShaderRead);
            graph.Use(_multiScattering, GpuAccess.ShaderRead);
            graph.Use(_skyView, GpuAccess.ShaderRead);
            graph.Use(_fogScattering, GpuAccess.ShaderWrite);
            shadows.UseForSampling(graph);
            graph.Recorder.BindPipeline(pipelines.FogInject);
            graph.Recorder.PushConstants(header);
            graph.Recorder.Dispatch(
                FogGridX / FogThreads,
                (FogGridY + FogThreads - 1) / FogThreads,
                FogGridZ / FogThreads
            );
            graph.EndPass();

            graph.BeginComputePass("Fog: integrate");
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(_fogScattering, GpuAccess.ShaderRead);
            graph.Use(_fogIntegrated, GpuAccess.ShaderWrite);
            graph.Recorder.BindPipeline(pipelines.FogIntegrate);
            graph.Recorder.PushConstants(header);
            graph.Recorder.Dispatch(FogGridX / 8, (FogGridY + 7) / 8);
            graph.EndPass();
        }

        graph.BeginComputePass("Shade");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_lightTable, GpuAccess.ShaderRead);
        graph.Use(_froxelLists, GpuAccess.ShaderRead);
        graph.Use(_transmittance, GpuAccess.ShaderRead);
        graph.Use(_multiScattering, GpuAccess.ShaderRead);
        graph.Use(_skyView, GpuAccess.ShaderRead);
        graph.Use(_fogIntegrated, GpuAccess.ShaderRead);
        graph.Use(targets.Visibility, GpuAccess.ShaderRead);
        graph.Use(targets.Depth, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferNormal, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferBase, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferSurface, GpuAccess.ShaderRead);
        graph.Use(targets.Hdr, GpuAccess.ShaderWrite);
        graph.Use(instances, GpuAccess.ShaderRead);
        graph.Use(visibleClusters, GpuAccess.ShaderRead);
        cards.UseForReading(graph);
        shadows.UseForSampling(graph);
        field.UseForSampling(graph);
        gi.UseForSampling(graph);
        gi.UseForCards(graph);
        translucency.UseForSampling(graph);
        graph.Recorder.BindPipeline(usesRayQuery ? pipelines.ShadeLightingRayQuery : pipelines.ShadeLighting);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(targets.Width, pipelines.TileSize), Groups(targets.Height, pipelines.TileSize));
        graph.EndPass();
    }

    private static uint Groups(int count, uint size) => (uint)((count + size - 1) / size);

    private Result<GpuTexture> MakeLut(string name, int width, int height, int depth) =>
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
}
