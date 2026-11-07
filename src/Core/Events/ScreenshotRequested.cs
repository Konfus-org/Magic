namespace Magic.Events;

/// <summary>
/// The console's <c>screenshot</c> command, as a message: whatever draws the frame saves what it drew as a PNG at
/// <paramref name="Path"/>, under <c>Screenshots/</c> beside Magic, and logs <c>Screenshot: &lt;path&gt;</c> once
/// the file is there.
/// </summary>
public record struct ScreenshotRequested(string Path) : IEvent
{
    public bool IsHandled { get; set; }
}
