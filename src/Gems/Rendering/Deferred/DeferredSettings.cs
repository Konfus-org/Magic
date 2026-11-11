using System.ComponentModel;
using Magic.Input;

namespace Magic.Deferred;

/// <summary>
/// What the renderer shows of a frame, and its debug window with its keys.
/// </summary>
[Category("Debug")]
public sealed record DeferredSettings
{
    /// <summary>
    /// The part of the frame the window shows: Final is the finished frame, any other one part of the GBuffer or the
    /// light alone.
    /// </summary>
    public DebugView RenderView { get; init; } = DebugView.Final;

    /// <summary>
    /// Whether the Rendering window, which chooses the view, is open. It needs a gem that provides the debug UI.
    /// </summary>
    public bool IsRenderWindowOpen { get; init; }

    /// <summary>
    /// The key that opens and closes the Rendering window.
    /// </summary>
    public Control RenderWindowKey { get; init; } = Control.KeyF2;

    /// <summary>
    /// The key that shows the view before the current one.
    /// </summary>
    public Control PreviousViewKey { get; init; } = Control.KeyF5;

    /// <summary>
    /// The key that shows the view after the current one.
    /// </summary>
    public Control NextViewKey { get; init; } = Control.KeyF6;

    /// <summary>
    /// Metres from the camera at which the Depth view turns black; nearer is lighter, on a log scale.
    /// </summary>
    public float DepthViewFar { get; init; } = 1000;
}
