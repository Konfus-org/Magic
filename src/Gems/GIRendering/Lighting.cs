using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The view's surfaces lit into the HDR picture, and the sky drawn where nothing was: the lighting constants, naming
/// the sky's tables, the lights and the shadow mask, uploaded before the view is drawn, and the shading after them.
/// </summary>
internal sealed class Lighting(AssetManager assets)
{
    private readonly ShaderEntry _shadeShader = PassShaders.GetEntry(assets, "ShadeSurfaces", "ShadeSurfaces") with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };

    public ImmutableArray<ShaderEntry> Shaders => [_shadeShader];

    /// <summary>
    /// The lighting constants asked for and uploaded, in a transfer pass the caller opened, after the sky's tables, the
    /// lights and, on a GPU that traces rays, the shadow mask were asked for; the buffer they are in.
    /// </summary>
    public Result<GpuBuffer> Prepare(
        in RenderView view,
        EcsSnapshot<Sky> skies,
        RenderCamera camera,
        RenderSun sun,
        in SkyTables sky,
        in LightBuffers lights,
        ShadowBuffers? shadows
    )
    {
        Result<GpuBuffer> made = view.Graph.Buffer(
            GpuDescriptions.GetBuffer("Lighting constants", LightingConstants.Bytes)
        );
        if (!made.Ok)
            return made;

        Result<GpuSampler> sampler = view.Graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (!sampler.Ok)
            return Result.Failure($"the lighting sampler was not made: {sampler.Error}");

        LightingConstants constants = new()
        {
            SunIlluminance = new Vector4(sun.Radiance, 0),
            CameraAltitude = new Vector4(Math.Max(camera.Transform.Position.Y, 0), 0, 0, 0),
            TransmittanceLut = sky.Transmittance.Id,
            MultiScatteringLut = sky.MultiScattering.Id,
            SkyViewLut = sky.View.Id,
            LutSampler = sampler.Value.Id,
            HasSky = skies.Count > 0 ? 1u : 0u,
            Hdr = view.Hdr.Id,
            Lights = lights.Table.Id,
            LightCount = (uint)lights.Count,
            LightTiles = lights.Tiles.Id,
            ShadowMask = shadows?.Mask.Id ?? 0,
            TracedShadows = shadows?.Traced.Id ?? 0,
            SunTiles = shadows?.SunTiles.Id ?? 0,
        };
        view.Graph.Upload(made.Value, [constants]);

        return made;
    }

    /// <summary>
    /// Every pixel's surface shaded from the sky's tables, its tile's lights and, on a GPU that traces rays, its
    /// shadows, and the sky where there is none.
    /// </summary>
    public void Record(
        in RenderView view,
        in GpuConstants header,
        GpuBuffer constants,
        in SkyTables sky,
        in LightBuffers lights,
        ShadowBuffers? shadows
    )
    {
        ComputePass shade = view
            .Graph.Compute("Shade surfaces", _shadeShader, header)
            .Reads(
                view.Constants,
                constants,
                sky.Transmittance,
                sky.MultiScattering,
                sky.View,
                lights.Table,
                lights.Tiles,
                view.Visibility,
                view.Depth,
                view.GBufferNormal,
                view.GBufferBase,
                view.GBufferSurface
            );
        if (shadows is { } shadowed)
            shade.Reads(shadowed.Mask);

        shade.Writes(view.Hdr).DispatchOver(view.Width, view.Height);
    }
}
