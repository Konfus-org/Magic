using System.Collections.Immutable;
using Magic.Assets;
using Magic.Graphics;

namespace Magic.Deferred;

/// <summary>
/// The view's surfaces lit into the HDR picture, and the sky drawn where nothing was, shadowed by the frame's shadow
/// mask and lit indirectly by the frame's indirect light where there are any; then the nearest translucent surface
/// shaded over it.
/// </summary>
internal sealed class Lighting(AssetManager assets)
{
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
    /// Every pixel's surface shaded from the sky's tables, its tile's lights and <paramref name="shadows"/> where there
    /// are any, and from <paramref name="indirect"/>, or the sky where nothing traced it this frame; then the
    /// nearest glass over it, lit by the light around it from the probes where there are any, and its rays through
    /// <paramref name="traced"/> where there is one; and the picture seen through <paramref name="air"/> where something
    /// filled it.
    /// </summary>
    public void Record(
        in RenderView view,
        in GpuConstants header,
        ShadowMask? shadows,
        TracedWorld? traced,
        Air? air,
        ReadOnlySpan<GpuResource> indirect
    )
    {
        ComputePass shade = view
            .Graph.Compute("Shade surfaces", _shadeShader, header, RenderStage.Shade)
            .Reads(
                view.Gpu.Frame,
                view.Gpu.SkyTransmittance,
                view.Gpu.SkyMultiScattering,
                view.Gpu.SkyView,
                view.Gpu.LightTable,
                view.Gpu.LightTiles,
                view.Gpu.Depth,
                view.Gpu.GBufferNormal,
                view.Gpu.GBufferBase,
                view.Gpu.GBufferSurface
            );
        if (shadows is { } shadowed)
            shade.Reads(shadowed.Mask, shadowed.SunTint);

        shade.Reads(indirect);

        shade.Writes(view.Gpu.Hdr).DispatchOver(view.Gpu.Width, view.Gpu.Height);

        // The nearest pane over the shaded picture, from what the resolve left in the glass targets.
        ComputePass translucent = view
            .Graph.Compute("Shade translucent", _translucentShader, header, RenderStage.Shade)
            .Reads(
                view.Gpu.Frame,
                view.Gpu.SkyTransmittance,
                view.Gpu.SkyMultiScattering,
                view.Gpu.SkyView,
                view.Gpu.GlassDepth,
                view.Gpu.GlassBase,
                view.Gpu.GlassNormal,
                view.Gpu.GlassSurface,
                view.Gpu.Depth
            );
        translucent.Reads(indirect);

        if (traced is { } world)
            translucent.Reads(world.HitMaterials, view.Gpu.Meshes, view.Gpu.Instances);

        if (air is { } filled)
            translucent.Reads(filled.Volume);

        translucent.Writes(view.Gpu.Hdr).DispatchOver(view.Gpu.Width, view.Gpu.Height);
    }
}
