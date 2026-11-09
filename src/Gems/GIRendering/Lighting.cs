using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The view's surfaces lit into the HDR picture, and the sky drawn where nothing was: the frame's lights and shadows,
/// naming the sky's tables, the light table and the shadow mask, uploaded before the view is drawn, and the shading
/// after them, lit indirectly by the traced diffuse light where there is any; then the nearest translucent surface
/// shaded over it.
/// </summary>
internal sealed class Lighting(AssetManager assets, GpuResources resources)
{
    private const float SkyBrightness = 0.3f;

    private readonly ShaderEntry _shadeShader = PassShaders.GetEntry(assets, "ShadeSurfaces", "ShadeSurfaces") with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };

    private readonly ShaderEntry _translucentShader = PassShaders.GetEntry(
        assets,
        "ShadeTranslucent",
        "ShadeTranslucent"
    ) with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };

    public ImmutableArray<ShaderEntry> Shaders => [_shadeShader, _translucentShader];

    /// <summary>
    /// The frame's lights and shadows uploaded into the frame's buffer, in a transfer pass the caller opened, after the
    /// sky's tables, the lights and, on a GPU that traces rays, the shadow mask were asked for.
    /// </summary>
    public static Result Upload(
        in RenderView view,
        EcsSnapshot<Sky> skies,
        RenderCamera camera,
        RenderSun sun,
        in SkyTables sky,
        in LightBuffers lights,
        ShadowBuffers? shadows
    )
    {
        Result<GpuSampler> sampler = view.Graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (!sampler.Ok)
            return Result.Failure($"the lighting sampler was not made: {sampler.Error}");

        GpuLights frameLights = new()
        {
            SunDirection = new Vector4(sun.Direction, 0),
            SunIlluminance = new Vector4(sun.Radiance, 0),
            SkyRadiance = new Vector4(SkyBrightness * 0.8f, SkyBrightness * 0.9f, SkyBrightness, 0),
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
        GpuShadows frameShadows = new()
        {
            Mask = shadows?.Mask.Id ?? 0,
            Traced = shadows?.Traced.Id ?? 0,
            SunTiles = shadows?.SunTiles.Id ?? 0,
            SunTint = shadows?.SunTint.Id ?? 0,
        };
        view.Graph.Upload(view.Constants, [frameLights], GpuLights.Offset);
        view.Graph.Upload(view.Constants, [frameShadows], GpuShadows.Offset);

        return Result.Success();
    }

    /// <summary>
    /// Every pixel's surface shaded from the sky's tables, its tile's lights and, on a GPU that traces rays, its
    /// shadows, its traced diffuse light and what it reflects, and the sky where there is none; then the nearest glass
    /// over it, lit by the light around it from <paramref name="probes"/> where there are any, and its rays through
    /// <paramref name="world"/> on a GPU that traces them.
    /// </summary>
    public void Record(
        in RenderView view,
        in GpuConstants header,
        in SkyTables sky,
        in LightBuffers lights,
        ShadowBuffers? shadows,
        TracedDiffuseBuffers? diffuse,
        TracedReflectionBuffers? reflections,
        ProbeBuffers? probes,
        TracedWorld? world
    )
    {
        ComputePass shade = view
            .Graph.Compute("Shade surfaces", _shadeShader, header)
            .Reads(
                view.Constants,
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
            shade.Reads(shadowed.Mask, shadowed.SunTint);

        if (diffuse is { } traced)
            shade.Reads(traced.Light);

        if (reflections is { } reflected)
            shade.Reads(reflected.Light);

        shade.Writes(view.Hdr).DispatchOver(view.Width, view.Height);

        // The nearest pane over the shaded picture, from what the resolve left in the glass targets.
        ComputePass translucent = view
            .Graph.Compute("Shade translucent", _translucentShader, header)
            .Reads(
                view.Constants,
                sky.Transmittance,
                sky.MultiScattering,
                sky.View,
                view.GlassDepth,
                view.GlassBase,
                view.GlassNormal,
                view.GlassSurface,
                view.Depth
            );
        if (probes is { } probed)
            translucent.Reads(probed.Probes, probed.Depth);

        if (world is { } rays)
            translucent.Reads(rays.HitMaterials, resources.Meshes, view.Instances);

        translucent.Writes(view.Hdr).DispatchOver(view.Width, view.Height);
    }
}
