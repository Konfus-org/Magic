namespace Magic.Contexts.CmdLine;

/// <summary>
/// What to run and how: the command line's options, as plain data. A gem can ask for it in its constructor, as
/// a platform gem does to open no window when <see cref="IsHeadless"/>.
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
    /// How long to run before stopping, by the frame clock, which goes on across a restart; by default the run goes on
    /// until a gem stops it. A ceiling for a run that should have stopped sooner, as frames take no fixed time; a run
    /// that must stop at the same point every time counts frames instead.
    /// </summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.MaxValue;

    /// <summary>
    /// The run has no window: Ctrl+C stops it cleanly, ending its gems, instead of ending the process at once.
    /// </summary>
    public bool IsHeadless { get; init; }

    /// <summary>
    /// Gem settings that win over the presets' (--set), each a <c>Gem.path=value</c> line as given.
    /// </summary>
    public IReadOnlyList<string> Settings { get; init; } = [];

    /// <summary>
    /// The preset applied over the default one (--preset): <c>Presets/&lt;Preset&gt;.preset</c>, the project's or the
    /// engine's. Empty for the default alone.
    /// </summary>
    public string Preset { get; init; } = string.Empty;

    /// <summary>
    /// The run opens each of the project's domains in turn, each once the last is shown, then stops (--bake): what they
    /// use is made and cached, as a Release build bakes it beside the project.
    /// </summary>
    public bool IsBaking { get; init; }
}
