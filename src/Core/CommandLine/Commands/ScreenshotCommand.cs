using System.CommandLine;
using System.Globalization;
using Magic.Events;
using Magic.Files;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>screenshot</c>: asks whatever draws the frame to save the next one as a PNG under <c>Screenshots/</c> beside Magic.
/// </summary>
internal static class ScreenshotCommand
{
    public static Command Create(App app, IFileSystem files, EventCoordinator events)
    {
        Command screenshot = new(
            "screenshot",
            "Saves what the next frame draws as a PNG under Screenshots/ beside Magic"
        );
        screenshot.SetAction(_ => RequestScreenshot());

        return screenshot;

        void RequestScreenshot()
        {
            if (!CommandLookups.IsProjectOpen(app, "screenshot"))
                return;

            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string path = files.Combine(AppContext.BaseDirectory, "Screenshots", $"{name}.png");
            events.Publish(new ScreenshotRequested(path));
        }
    }
}
