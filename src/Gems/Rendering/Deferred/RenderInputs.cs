namespace Magic.Deferred;

/// <summary>
/// What the game thread's settings ask of a frame, copied as its frame ends and published for the render thread, which
/// reads nothing else of the game thread's.
/// </summary>
internal sealed class RenderInputs
{
    public DebugView DebugView { get; set; }
}
