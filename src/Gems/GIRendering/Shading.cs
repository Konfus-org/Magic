namespace GIRendering;

/// <summary>
/// The GBuffer lit into the HDR picture: the sun through its pages, the lights of each pixel's froxel, the global
/// illumination's probes and the air in front of it.
/// </summary>
internal sealed class Shading(
    GpuPipelines pipelines,
    Lighting lighting,
    SunShadows shadows,
    DistanceField field,
    GlobalIllumination gi,
    GlassShadow glassShadow,
    CardTable cards,
    Visibility visibility
)
{
    private readonly GpuPass _shadePass = pipelines.Compute(
        "Shade",
        "ShadeLighting",
        "ShadeLighting",
        PassShaders.Atmosphere | PassShaders.Tracer,
        isTiled: true
    );

    public void Record(in RenderView view, bool usesRayQuery)
    {
        FrameTargets targets = view.Targets;
        GpuPassRun shadeRun = _shadePass
            .Run(lighting.GetHeader(view.Constants), usesRayQuery: usesRayQuery)
            .Reads(
                view.Constants,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferBase,
                targets.GBufferSurface
            )
            .Writes(targets.Hdr)
            .Reads(view.Instances, visibility.VisibleClusters);
        lighting.UseForShading();
        cards.UseForReading();
        shadows.UseForSampling();
        field.UseForSampling();
        gi.UseForSampling();
        gi.UseForCards();
        glassShadow.UseForSampling();
        shadeRun.Dispatch(pipelines.GetTiles(targets.Width), pipelines.GetTiles(targets.Height));
    }
}
