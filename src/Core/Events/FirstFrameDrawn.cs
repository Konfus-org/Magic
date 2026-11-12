namespace Magic.Events;

/// <summary>
/// Tells that the renderer drew its first frame, after its shaders compiled.
/// </summary>
/// <remarks>
/// A listener can wait for it to know that frames show on the screen.
/// </remarks>
public record struct FirstFrameDrawn : IEvent
{
    public bool IsHandled { get; set; }
}
