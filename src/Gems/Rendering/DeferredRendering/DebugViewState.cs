using Magic.CommandLine;

namespace Magic.DeferredRendering;

/// <summary>
/// The part of the frame that the renderer shows now, and whether the window that chooses it is open.
/// </summary>
/// <remarks>
/// The keys of <see cref="DeferredRenderingSettings"/> and the Rendering window change it, and so does the console, as
/// <c>set DeferredRendering.RenderView Normal</c>. It is state of the run and is not saved. The game thread writes it,
/// and the render thread reads the view.
/// </remarks>
[CliPath("DeferredRendering")]
public sealed class DebugViewState
{
    /// <summary>
    /// The part of the frame the window shows: Final is the finished frame, any other one part of the GBuffer or the
    /// light alone.
    /// </summary>
    public DebugView RenderView { get; set; } = DebugView.Final;

    /// <summary>
    /// Metres from the camera at which the Depth view turns black; nearer is lighter, on a log scale.
    /// </summary>
    public float DepthViewFar { get; set; } = 1000;

    /// <summary>
    /// Whether the Rendering window, which chooses the view, is open. It needs a gem that provides the debug UI.
    /// </summary>
    public bool IsRenderWindowOpen { get; set; }
}
