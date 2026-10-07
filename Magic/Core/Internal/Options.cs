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

    [Option("root", HelpText = "Use this folder as the root (where Assets and Cache live; logs and screenshots go next to the executable) instead of the project folder.")]
    public string? Root { get; set; }

    [Option("gems", HelpText = "The engine gem folder the project's gems list is resolved against, instead of Gems next to the executable.")]
    public string? Gems { get; set; }

    [Option("lifetime", Default = 0L, HelpText = "Exit after this many frames. 0 runs until the window closes or Ctrl+C.")]
    public long Lifetime { get; set; }

    [Option("screenshots", Default = 0, HelpText = "How many screenshots to take, as PNGs in Screenshots next to the executable.")]
    public int Screenshots { get; set; }

    [Option("screenshot-delay", HelpText = "The frame the first screenshot is taken at the end of (the Screenshots.Delay setting).")]
    public long? ScreenshotDelay { get; set; }

    [Option("screenshot-interval", HelpText = "Frames between one screenshot and the next (the Screenshots.Interval setting).")]
    public long? ScreenshotInterval { get; set; }

    [Option("headless", HelpText = "Open no window; gems and systems still run. Ends on --lifetime or Ctrl+C.")]
    public bool Headless { get; set; }

    [Option("width", HelpText = "Main window width in pixels (the Window.Width setting).")]
    public int? Width { get; set; }

    [Option("height", HelpText = "Main window height in pixels (the Window.Height setting).")]
    public int? Height { get; set; }

    [Option("window-mode", HelpText = "Main window mode: windowed, fullscreen or borderless (the Window.Mode setting).")]
    public string? WindowMode { get; set; }

    [Option("log-level", Default = LogLevel.Debug, HelpText = "Drop log messages below this level: debug, information, warning, error or critical.")]
    public LogLevel LogLevel { get; set; }

    [Option("verbose", HelpText = "Also write verbose logs: periodic FPS and frame timings, chunk streaming. Off by default whatever --log-level is.")]
    public bool Verbose { get; set; }

    [Option("fail-on-error", HelpText = "Exit with code 2 if anything was logged at Error or above, so a script can tell a clean run from a noisy one.")]
    public bool FailOnError { get; set; }

    [Option("entry-point", HelpText = "Open this domain at start-up instead of the project's entry point: an asset path, like Cave/Cave.domain.")]
    public string? EntryPoint { get; set; }

    [Option("loading", HelpText = "Show this domain while another opens instead of the project's loading domain: an asset path, like Domains/Loading/Loading.domain.")]
    public string? Loading { get; set; }

    [Option("preset", HelpText = "Use this settings preset instead of the default: an asset path, like Presets/Toaster.preset.")]
    public string? Preset { get; set; }

    [Option("pipeline", HelpText = "Render with this pipeline instead of the default: an asset path, like Pipelines/Mine.pipeline.")]
    public string? Pipeline { get; set; }

    [Option("set", HelpText = "Override settings over the preset: --set Gpu.Vsync=false Lod.Bias=2 ShadowPlan.distance=500 (Owner.Property=value, space separated; Owner is a settings name or a pass's file name).")]
    public IEnumerable<string> Set { get; set; } = [];

    /// <summary>
    /// <see cref="Set"/>, after what the shorthand options stand for, each a gem's setting: the main window's
    /// (<c>--headless</c>, <c>--width</c>, <c>--height</c>, <c>--window-mode</c>) the platform gem's, <c>--screenshots</c>
    /// and its two companions the screenshot gem's, <c>--entry-point</c> the world's, <c>--loading</c> the loading
    /// screen's, <c>--preset</c> the presets gem's and <c>--pipeline</c> the renderer's. Only what was given: a setting the
    /// command line leaves out keeps the preset's value, or its own default.
    /// </summary>
    public IEnumerable<string> SettingLines
    {
        get
        {
            List<string> lines = [];
            if (Headless)
                lines.Add("Window.Open=false");
            if (Width is { } width)
                lines.Add($"Window.Width={width}");
            if (Height is { } height)
                lines.Add($"Window.Height={height}");
            if (WindowMode is { } mode)
                lines.Add($"Window.Mode={mode}");
            if (Screenshots > 0)
                lines.Add($"Screenshots.Count={Screenshots}");
            if (ScreenshotDelay is { } delay)
                lines.Add($"Screenshots.Delay={delay}");
            if (ScreenshotInterval is { } interval)
                lines.Add($"Screenshots.Interval={interval}");

            // A path is set as text: Settings reads what is not JSON as a string.
            if (EntryPoint is not null)
                lines.Add($"World.EntryPoint={EntryPoint}");
            if (Loading is not null)
                lines.Add($"LoadingScreen.Domain={Loading}");
            if (Preset is not null)
                lines.Add($"Preset.Path={Preset}");
            if (Pipeline is not null)
                lines.Add($"Deferred.Pipeline={Pipeline}");

            return [.. lines, .. Set];
        }
    }

    /// <summary>
    /// The first thing wrong with the values, or null when they can be run.
    /// </summary>
    public string? Validate()
    {
        if (Lifetime < 0)
            return "--lifetime cannot be negative.";
        if (Screenshots < 0)
            return "--screenshots cannot be negative.";
        if (ScreenshotDelay is < 1)
            return "--screenshot-delay must be at least 1: there is nothing to capture before the first frame.";
        if (ScreenshotInterval is < 1)
            return "--screenshot-interval must be at least 1.";
        if (Width is < 1 || Height is < 1)
            return "--width and --height must be positive.";
        if (LogLevel == LogLevel.Verbose)
            return "--log-level verbose is not a level; pass --verbose instead.";
        if (Headless && Screenshots > 0)
            return "--screenshots needs a window; drop --headless.";

        foreach (string set in Set)
        {
            int eq = set.IndexOf('=');
            if (eq <= 0 || !set[..eq].Contains('.'))
                return $"--set {set}: expected Section.Key=value.";
        }

        return null;
    }
}
