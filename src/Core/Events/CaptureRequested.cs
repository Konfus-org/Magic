namespace Magic.Events;

/// <summary>
/// Asks the GPU provider to capture the next <paramref name="Frames"/> frames with RenderDoc, into files whose names
/// start with <paramref name="PathTemplate"/>.
/// </summary>
/// <remarks>
/// The path is in the <c>Captures/</c> folder beside Magic. RenderDoc adds the frame number and <c>.rdc</c> to it.
/// </remarks>
public record struct CaptureRequested(string PathTemplate, int Frames) : IEvent
{
    public bool IsHandled { get; set; }
}
