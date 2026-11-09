namespace Magic.Deferred;

/// <summary>
/// What the game thread's settings ask of a frame, copied as its frame ends and published for the render thread, which
/// reads nothing else of the game thread's.
/// </summary>
internal sealed class RenderInputs
{
    public DebugView DebugView { get; set; }

    /// <summary>
    /// The pixel radius on screen under which an instance hands off to its impostor; 0 hands off by distance alone.
    /// </summary>
    public float ImpostorPixels { get; set; }

    /// <summary>
    /// The distance in metres to an instance past which it hands off to its impostor; 0 hands off by size alone.
    /// </summary>
    public float ImpostorDistance { get; set; }
}
