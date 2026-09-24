using CommandLine;
using Magic.Interfaces;

namespace Magic;

/// <summary>
/// The command line, as CommandLineParser fills it in. Paths are resolved against the working directory
/// and every duration is a frame count, so a run is reproducible: the same arguments do the same thing
/// whatever the frame rate. <c>--help</c> and <c>--version</c> come with the parser.
/// </summary>
internal sealed class Options
{
    [Option("project", HelpText = "A project folder holding a .magic file, or the .magic file itself. The folder becomes the root. Without it the engine runs as its own project.")]
    public string? Project { get; set; }

    [Option("root", HelpText = "Use this folder as the root (where Assets, Cache, Logs and Screenshots live) instead of the project folder.")]
    public string? Root { get; set; }

    [Option("gems", HelpText = "Load gems from this folder instead of Gems next to the executable.")]
    public string? Gems { get; set; }

    [Option("lifetime", Default = 0L, HelpText = "Exit after this many frames. 0 runs until the window closes or Ctrl+C.")]
    public long Lifetime { get; set; }

    [Option("screenshots", Default = 0, HelpText = "How many screenshots to take, as PNGs in <root>/Screenshots.")]
    public int Screenshots { get; set; }

    [Option("screenshot-delay", Default = 1L, HelpText = "The frame the first screenshot is taken at the end of.")]
    public long ScreenshotDelay { get; set; }

    [Option("screenshot-interval", Default = 60L, HelpText = "Frames between one screenshot and the next.")]
    public long ScreenshotInterval { get; set; }

    [Option("headless", HelpText = "Open no window; gems and systems still run. Ends on --lifetime or Ctrl+C.")]
    public bool Headless { get; set; }

    [Option("width", Default = 800, HelpText = "Main window width in pixels.")]
    public int Width { get; set; }

    [Option("height", Default = 600, HelpText = "Main window height in pixels.")]
    public int Height { get; set; }

    [Option("window-mode", Default = WindowMode.Windowed, HelpText = "Main window mode: windowed, fullscreen or borderless.")]
    public WindowMode WindowMode { get; set; }

    [Option("log-level", Default = LogLevel.Debug, HelpText = "Drop log messages below this level: debug, information, warning, error or critical.")]
    public LogLevel LogLevel { get; set; }

    [Option("fail-on-error", HelpText = "Exit with code 2 if anything was logged at Error or above, so a script can tell a clean run from a noisy one.")]
    public bool FailOnError { get; set; }

    /// <summary>The first thing wrong with the values, or null when they can be run.</summary>
    public string? Validate()
    {
        if (Lifetime < 0)
            return "--lifetime cannot be negative.";
        if (Screenshots < 0)
            return "--screenshots cannot be negative.";
        if (ScreenshotDelay < 1)
            return "--screenshot-delay must be at least 1: there is nothing to capture before the first frame.";
        if (ScreenshotInterval < 1)
            return "--screenshot-interval must be at least 1.";
        if (Width < 1 || Height < 1)
            return "--width and --height must be positive.";
        if (Headless && Screenshots > 0)
            return "--screenshots needs a window; drop --headless.";
        return null;
    }

    /// <summary>The frame the last screenshot lands on, or 0 when none are taken.</summary>
    public long LastScreenshotFrame => Screenshots == 0 ? 0 : ScreenshotDelay + ((Screenshots - 1) * ScreenshotInterval);
}
