using System.CommandLine;
using System.Globalization;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;

namespace Magic.CommandLine.Commands;

/// <summary>
/// <c>capture</c>: asks the GPU provider to capture the next frames with RenderDoc, into <c>Captures/</c> beside Magic.
/// It captures only when RenderDoc started Magic.
/// </summary>
internal static class CaptureCommand
{
    public static Command Create(App app, IFileSystem files, EventCoordinator events)
    {
        Option<int> frames = new("--frames")
        {
            Description = "How many frames in a row the capture holds",
            DefaultValueFactory = _ => 1,
        };
        Command capture = new(
            "capture",
            "Captures the next frames with RenderDoc, when RenderDoc started Magic, into Captures/ beside Magic"
        )
        {
            frames,
        };
        capture.SetAction(parsed => RequestCapture(parsed.GetValue(frames)));

        return capture;

        void RequestCapture(int count)
        {
            if (!CommandLookups.IsProjectOpen(app, "capture"))
                return;

            if (count < 1)
            {
                Log.Error($"capture --frames {count} took nothing: give 1 or more frames.");

                return;
            }

            // RenderDoc makes the folder when it starts the capture.
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
            events.Publish(new CaptureRequested(files.Combine(AppContext.BaseDirectory, "Captures", name), count));
        }
    }
}
