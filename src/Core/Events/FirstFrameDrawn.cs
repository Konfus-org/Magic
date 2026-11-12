namespace Magic.Events;

/// <summary>
/// Tells that the renderer drew its first frame, after its shaders compiled.
/// </summary>
public record struct FirstFrameDrawn : IEvent
{
    public bool IsHandled { get; set; }
}
