using System.ComponentModel;

namespace Magic.Deferred;

/// <summary>
/// What the renderer shows of a frame, and its debug window.
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
}
