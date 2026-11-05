using Magic.Gpu;
using Magic.Rendering;

namespace Magic.GIRendering;

/// <summary>
/// The panes drawn after the lighting, forward, over the opaque depth, in any order: their absorbance and the moments of
/// their depths added up first, then each pane's light, for the tonemap to compose over the picture.
/// </summary>
internal sealed class GlassDraws(
    RenderGraph graph,
    GpuPipelines pipelines,
    GlassShadow glassShadow,
    SunShadows shadows,
    Lighting lighting,
    GlobalIllumination gi
)
{
    private readonly GpuPass _momentsPass = pipelines.Draw(
        "Translucent: moments",
        "DrawTranslucent",
        "DrawTranslucentVertex",
        "TranslucentMomentsPixel",
        state =>
            state with
            {
                ColorFormats = [GpuFormat.R16G16B16A16Sfloat, GpuFormat.R16G16B16A16Sfloat],
                DepthFormat = GpuFormat.D32Sfloat,
                Depth = DepthMode.TestOnly,
                Blend = BlendMode.Additive,
                Cull = CullMode.None,
            },
        PassShaders.Glass
    );

    private readonly GpuPass _resolvePass = pipelines.Draw(
        "Translucent: resolve",
        "DrawTranslucent",
        "DrawTranslucentVertex",
        "TranslucentResolvePixel",
        state =>
            state with
            {
                ColorFormats = [GpuFormat.R16G16B16A16Sfloat, GpuFormat.R16G16Sfloat],
                DepthFormat = GpuFormat.D32Sfloat,
                Depth = DepthMode.TestOnly,
                Blend = BlendMode.Additive,
                Cull = CullMode.None,
            },
        PassShaders.Glass | PassShaders.Atmosphere
    );

    public void Record(in DrawList draws, FrameTargets targets)
    {
        GpuPassRun momentsRun = _momentsPass.Run(
            draws.Header,
            new GpuRenderTargets
            {
                Colors =
                [
                    new GpuColorTarget { Texture = targets.OpticalDepth, Load = TargetLoad.Clear },
                    new GpuColorTarget { Texture = targets.Moments, Load = TargetLoad.Clear },
                ],
                Depth = new GpuDepthTarget { Texture = targets.Depth },
            }
        );
        glassShadow.UseDraws(draws);
        momentsRun
            .Reads(lighting.Constants)
            .DrawIndirectCount(
                draws.Arguments,
                draws.ArgumentsOffset,
                draws.Counts,
                draws.CountOffset,
                draws.MaxDraws,
                draws.ArgumentsStride
            );

        GpuPassRun resolveRun = _resolvePass.Run(
            draws.Header,
            new GpuRenderTargets
            {
                Colors =
                [
                    new GpuColorTarget { Texture = targets.Accumulation, Load = TargetLoad.Clear },
                    new GpuColorTarget { Texture = targets.Offsets, Load = TargetLoad.Clear },
                ],
                Depth = new GpuDepthTarget { Texture = targets.Depth },
            }
        );
        glassShadow.UseDraws(draws);
        resolveRun.Reads(targets.OpticalDepth, targets.Moments, glassShadow.Texture);
        shadows.UseForSampling();
        lighting.UseForShading();
        gi.UseForSampling();
        resolveRun.DrawIndirectCount(
            draws.Arguments,
            draws.ArgumentsOffset,
            draws.Counts,
            draws.CountOffset,
            draws.MaxDraws,
            draws.ArgumentsStride
        );
    }

    /// <summary>
    /// Says a pass composes the panes over the picture: what the tonemap declares.
    /// </summary>
    public void UseForComposite(FrameTargets targets)
    {
        graph.Use(glassShadow.Constants, GpuAccess.ShaderRead);
        graph.Use(targets.OpticalDepth, GpuAccess.ShaderRead);
        graph.Use(targets.Accumulation, GpuAccess.ShaderRead);
        graph.Use(targets.Offsets, GpuAccess.ShaderRead);
    }
}
