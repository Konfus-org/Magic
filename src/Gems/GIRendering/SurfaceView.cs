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
/// The output drawn from the lit picture, or from one of the GBuffer's properties when a debug view asks.
/// </summary>
internal sealed class SurfaceView(GpuPipelines pipelines)
{
    private readonly GpuPass _showPass = pipelines.Compute("Show surface", "ShowSurface", "ShowSurface");

    public void Record(in RenderView view, DebugView debugView, float exposure)
    {
        FrameTargets targets = view.Targets;
        _showPass
            .Run(
                new GpuConstants(
                    view.Constants.Id,
                    (uint)debugView,
                    targets.Hdr.Id,
                    BitConverter.SingleToUInt32Bits(exposure)
                )
            )
            .Reads(
                view.Constants,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferBase,
                targets.GBufferSurface,
                targets.Velocity,
                targets.Hdr
            )
            .Writes(targets.Output)
            .DispatchOver(targets.Width, targets.Height);
    }
}
