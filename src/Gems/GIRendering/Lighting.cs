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
/// the sky's tables, the lights, the shadow mask and the traced reflections, uploaded before the view is drawn, and
/// the shading after them, lit indirectly by the traced diffuse light where there is any; then the nearest glass shaded
/// over it.
/// </summary>
internal sealed class Lighting(AssetManager assets, GpuResources resources)
{
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
    /// The lighting constants asked for and uploaded, in a transfer pass the caller opened, after the sky's tables, the
    /// lights and, on a GPU that traces rays, the shadow mask and the traced reflections were asked for; the buffer they
    /// are in.
    /// </summary>
    public Result<GpuBuffer> Prepare(
        in RenderView view,
        EcsSnapshot<Sky> skies,
        RenderCamera camera,
        RenderSun sun,
        in SkyTables sky,
        in LightBuffers lights,
        ShadowBuffers? shadows,
        TracedReflectionBuffers? reflections
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
            SunTint = shadows?.SunTint.Id ?? 0,
            Reflections = reflections?.Light.Id ?? 0,
        };
        view.Graph.Upload(made.Value, [constants]);

        return made;
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
        GpuBuffer constants,
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
            .Graph.Compute("Shade surfaces", _shadeShader, header with { W = diffuse?.Constants.Id ?? 0 })
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
            shade.Reads(shadowed.Mask, shadowed.SunTint);

        if (diffuse is { } traced)
            shade.Reads(traced.Constants, traced.Light);

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
                .Graph.Compute("Shade glass", entry, header with { Y = probes?.Constants.Id ?? 0, W = (uint)shader })
                .Reads(
                    view.Constants,
                    constants,
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
                glass.Reads(probed.Constants, probed.Probes, probed.Depth);

            if (world is { } rays)
                glass.Reads(rays.HitMaterials, resources.LeafClusters);

            glass.Writes(view.Hdr).DispatchOver(view.Width, view.Height);
        }
    }
}
