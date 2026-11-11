using System.Numerics;
using Magic.Assets;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GpuWorld;

/// <summary>
/// The lights' row of the frame's buffer, which the sky and every lit pass read.
/// </summary>
internal static class FrameLights
{
    /// <summary>
    /// The frame's lights uploaded into the frame's buffer, after the models' row: the sun, the open sky at
    /// <paramref name="skyBrightness"/>, the sky and its tables, and the light table and its tiles.
    /// </summary>
    public static Result Upload(
        RenderGraph graph,
        GpuBuffer frame,
        EcsSnapshot<Sky> skies,
        RenderCamera camera,
        RenderSun sun,
        float skyBrightness,
        in SkyTables sky,
        in LightBuffers lights
    )
    {
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (!sampler.Ok)
            return Result.Failure($"the lighting sampler was not made: {sampler.Error}");

        GpuLights frameLights = new()
        {
            SunDirection = new Vector4(sun.Direction, 0),
            SunIlluminance = new Vector4(sun.Radiance, 0),
            SkyRadiance = new Vector4(skyBrightness * 0.8f, skyBrightness * 0.9f, skyBrightness, 0),
            CameraAltitude = Math.Max(camera.Transform.Position.Y, 0),
            TransmittanceLut = sky.Transmittance.Id,
            MultiScatteringLut = sky.MultiScattering.Id,
            SkyViewLut = sky.View.Id,
            LutSampler = sampler.Value.Id,
            HasSky = skies.Count > 0 ? 1u : 0u,
            Table = lights.Table.Id,
            Count = (uint)lights.Count,
            Tiles = lights.Tiles.Id,
        };
        graph.Upload(frame, [frameLights], GpuLights.Offset);

        return Result.Success();
    }
}
