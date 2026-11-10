namespace Magic.Events;

/// <summary>
/// The console's <c>screenshot</c> command, as a message: whatever draws the frame saves what it drew as a PNG at
/// <paramref name="Path"/>, under <c>Screenshots/</c> beside Magic, and prints the path as the command's result once
/// the file is there.
/// </summary>
public record struct ScreenshotRequested(string Path) : IEvent
{
    public bool IsHandled { get; set; }
}
