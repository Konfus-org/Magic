using Magic.Gpu;

namespace Magic.GIRendering;

/// <summary>
/// What the output shows of a frame's surfaces, the twin of <c>ShowSurface.slang</c>'s.
/// </summary>
internal enum DebugView : uint
{
    /// <summary>
    /// The finished frame.
    /// </summary>
    Final,

    /// <summary>
    /// The surface's base colour.
    /// </summary>
    Albedo,

    /// <summary>
    /// The surface's world normal, each axis from -1 to 1 shown from black to full.
    /// </summary>
    Normal,

    /// <summary>
    /// How far the surface is from the camera, on a log scale: white at the camera.
    /// </summary>
    Depth,

    Roughness,

    Metalness,

    /// <summary>
    /// The material's own occlusion.
    /// </summary>
    Occlusion,

    /// <summary>
    /// How far the surface moved on the screen since last frame.
    /// </summary>
    Velocity,
}

/// <summary>
/// The output drawn from the GBuffer: the surfaces' colours, or one of their other properties when a debug view asks.
/// </summary>
internal sealed class SurfaceView(GpuPipelines pipelines)
{
    private readonly GpuPass _showPass = pipelines.Compute("Show surface", "ShowSurface", "ShowSurface", isTiled: true);

    public void Record(in RenderView view, DebugView debugView)
    {
        FrameTargets targets = view.Targets;
        _showPass
            .Run(new GpuConstants(view.Constants.Id, (uint)debugView))
            .Reads(
                view.Constants,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferBase,
                targets.GBufferSurface,
                targets.Velocity
            )
            .Writes(targets.Output)
            .Dispatch(pipelines.GetTiles(targets.Width), pipelines.GetTiles(targets.Height));
    }
}
