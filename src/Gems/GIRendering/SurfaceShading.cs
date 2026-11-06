namespace Magic.GIRendering;

/// <summary>
/// The view's surfaces lit into the HDR picture, and the sky drawn where nothing was.
/// </summary>
internal sealed class SurfaceShading(GpuPipelines pipelines, Lighting lighting)
{
    private readonly GpuPass _shadePass = pipelines.Compute(
        "Shade surfaces",
        "ShadeSurfaces",
        "ShadeSurfaces",
        PassShaders.Atmosphere
    );

    public void Record(in RenderView view)
    {
        FrameTargets targets = view.Targets;
        GpuPassRun run = _shadePass.Run(lighting.GetHeader(view.Constants));
        lighting.UseForShading();
        run.Reads(
                view.Constants,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferBase,
                targets.GBufferSurface
            )
            .Writes(targets.Hdr)
            .DispatchOver(targets.Width, targets.Height);
    }
}
