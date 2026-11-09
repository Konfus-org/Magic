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
/// after them, lit indirectly by the traced diffuse light where there is any; then the nearest glass shaded
/// over it.
/// </summary>
internal sealed class Lighting(AssetManager assets, GpuResources resources)
{
    private const float SkyBrightness = 0.3f;

    private readonly ShaderEntry _shadeShader = PassShaders.GetEntry(assets, "ShadeSurfaces", "ShadeSurfaces") with
    {
        TypeArguments = [PassShaders.GetAtmosphere(assets)],
    };

    private readonly ShaderEntry _glassShader = PassShaders.GetEntry(assets, "ShadeGlass", "ShadeGlass");

    /// <summary>
    /// The glass entry specialised to each translucent material shader and the sky, by the number rows name the shader
    /// by: kept, so a frame makes none.
    /// </summary>
    private readonly Dictionary<int, ShaderEntry> _glassShaders = [];

    public ImmutableArray<ShaderEntry> Shaders => [_shadeShader];

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
    /// The glass entry for each translucent material shader the resources have numbered, one made for each newly
    /// numbered.
    /// </summary>
    public IReadOnlyCollection<ShaderEntry> GetGlassShaders()
    {
        for (int shader = 0; shader < resources.MaterialShaders.Count; shader++)
        {
            if (resources.IsTranslucent(shader) && !_glassShaders.ContainsKey(shader))
            {
                _glassShaders[shader] = _glassShader with
                {
                    TypeArguments = [resources.MaterialShaders[shader], PassShaders.GetAtmosphere(assets)],
                };
            }
        }

        return _glassShaders.Values;
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

        // The nearest glass over the shaded picture, a pass for each translucent material shader, which shades the
        // pixels whose rows it draws.
        GetGlassShaders();
        ImmutableArray<GpuResource> heapTextures = resources.GetHeapTextures(view.Graph);
        foreach ((int shader, ShaderEntry entry) in _glassShaders)
        {
            ComputePass glass = view
                .Graph.Compute("Shade glass", entry, header with { W = (uint)shader })
                .Reads(
                    view.Constants,
                    sky.Transmittance,
                    sky.MultiScattering,
                    sky.View,
                    view.Instances,
                    view.Materials,
                    resources.ClusterPool,
                    resources.VertexPool,
                    resources.IndexPool,
                    view.Cull.VisibleClusters,
                    view.GlassVisibility,
                    view.Depth
                )
                .Reads(heapTextures.AsSpan());
            if (probes is { } probed)
                glass.Reads(probed.Probes, probed.Depth);

            if (world is { } rays)
                glass.Reads(rays.HitMaterials, resources.Meshes);

            glass.Writes(view.Hdr).DispatchOver(view.Width, view.Height);
        }
    }
}
