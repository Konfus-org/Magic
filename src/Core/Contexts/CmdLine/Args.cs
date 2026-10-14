using Magic.Contexts.Settings;

namespace Magic.Contexts.CmdLine;

/// <summary>
/// What to run and how: the command line's options, as plain data. A gem can ask for it in its constructor, as
/// a platform gem does to open no window when <see cref="Headless"/>.
/// </summary>
public sealed class Args
{
    /// <summary>
    /// The project: its .magic file, or the folder holding it.
    /// </summary>
    public required string ProjectPath { get; init; }

    /// <summary>
    /// How many frames to run before stopping; by default the run goes on until a gem stops it.
    /// </summary>
    public ulong FrameLimit { get; init; } = ulong.MaxValue;

    /// <summary>
    /// The run has no window: Ctrl+C stops it cleanly, ending its gems, instead of ending the process at once.
    /// </summary>
    public bool Headless { get; init; }

    /// <summary>
    /// Gem settings that win over the presets' (--set).
    /// </summary>
    public IReadOnlyList<Setting> Settings { get; init; } = [];

    /// <summary>
    /// The preset applied over the default one (--preset): <c>Presets/&lt;Preset&gt;.preset</c>, the project's or the
    /// engine's. Empty for the default alone.
    /// </summary>
    public string Preset { get; init; } = string.Empty;
}
