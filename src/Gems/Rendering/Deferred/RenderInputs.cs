namespace Magic.Deferred;

/// <summary>
/// What the game thread's settings ask of a frame, copied as its frame ends and published for the render thread, which
/// reads nothing else of the game thread's.
/// </summary>
internal sealed class RenderInputs
{
    /// <summary>
    /// The settings the frame is drawn by.
    /// </summary>
    public DeferredSettings Settings { get; set; } = new();
}
