using System.CommandLine;
using System.Globalization;
using Magic.Events;
using Magic.Files;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>screenshot</c>: asks the part that draws the frame to save the next frame as a PNG under <c>Screenshots/</c>
/// beside Magic; with <c>--hdr</c>, its lit light before the exposure, as a PFM.
/// </summary>
internal static class ScreenshotCommand
{
    public static Command Create(App app, IFileSystem files, EventCoordinator events)
    {
        Option<bool> hdr = new("--hdr")
        {
            Description = "Save the frame's lit light before the exposure, in linear floats, as a PFM",
        };
        Command screenshot = new(
            "screenshot",
            "Saves what the next frame draws as a PNG under Screenshots/ beside Magic"
        )
        {
            hdr,
        };
        screenshot.SetAction(parsed => RequestScreenshot(parsed.GetValue(hdr) ? "pfm" : "png"));

        return screenshot;

        void RequestScreenshot(string extension)
        {
            if (!CommandLookups.IsProjectOpen(app, "screenshot"))
                return;

            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
            string path = files.Combine(GetFolder(files), $"{name}.{extension}");
            events.Publish(new ScreenshotRequested(path));
        }
    }

    /// <summary>
    /// The folder that a screenshot goes in, beside Magic.
    /// </summary>
    public static string GetFolder(IFileSystem files) => files.Combine(AppContext.BaseDirectory, "Screenshots");
}
