namespace Magic.Events;

/// <summary>
/// Asks the renderer to save the frame that it drew as a PNG file at <paramref name="Path"/>.
/// </summary>
/// <remarks>
/// The path is in the <c>Screenshots/</c> folder beside Magic. When the file exists, the renderer prints the path as
/// the result of the <c>screenshot</c> console command.
/// </remarks>
public record struct ScreenshotRequested(string Path) : IEvent
{
    public bool IsHandled { get; set; }
}
