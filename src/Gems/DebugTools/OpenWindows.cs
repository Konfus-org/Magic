using Magic.CommandLine;

namespace Magic.DebugTools;

/// <summary>
/// Which of the debug lines and windows show now.
/// </summary>
/// <remarks>
/// A key of <see cref="DebugToolsSettings"/> or the close button of a window changes one, and so does the console, as
/// <c>set DebugTools.IsStatsOpen true</c>. It is state of the run and is not saved. Game thread.
/// </remarks>
[CliPath("DebugTools")]
public sealed class OpenWindows
{
    /// <summary>
    /// Whether the line of the frame rate, the frame time and the memory shows in the top left corner. It shows from
    /// the start in a debug build.
    /// </summary>
#if DEBUG
    public bool IsCounterShown { get; set; } = true;
#else
    public bool IsCounterShown { get; set; }
#endif

    /// <summary>
    /// Whether the stats window, every instrument of the meters, is open.
    /// </summary>
    public bool IsStatsOpen { get; set; }

    /// <summary>
    /// Whether the window of every gem's settings is open, where each is changed and saved.
    /// </summary>
    public bool IsSettingsOpen { get; set; }
}
