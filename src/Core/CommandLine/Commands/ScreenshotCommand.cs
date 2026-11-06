using System.CommandLine;
using System.Globalization;
using Magic.Events;
using Magic.Files;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>screenshot</c>: asks whatever draws the frame to save the next one as a PNG under <c>Screenshots/</c> beside Magic.
/// </summary>
internal sealed class ScreenshotCommand : Command
{
    private readonly App _app;

    private readonly IFileSystem _files;

    private readonly EventCoordinator _events;

    public ScreenshotCommand(App app, IFileSystem files, EventCoordinator events)
        : base("screenshot", "Saves what the next frame draws as a PNG under Screenshots/ beside Magic")
    {
        _app = app;
        _files = files;
        _events = events;
        Action = new CliCommand(_ => RequestScreenshot());
    }

    private void RequestScreenshot()
    {
        if (!CommandLookups.IsProjectOpen(_app, "screenshot"))
            return;

        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string path = _files.Combine(AppContext.BaseDirectory, "Screenshots", $"{name}.png");
        _events.Publish(new ScreenshotRequested(path));
    }
}
