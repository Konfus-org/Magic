namespace Magic.Threading;

/// <summary>
/// The engine's own threads, each of which runs for the whole run and is named for its member, as a profiler or
/// debugger shows it. Background work is a task, not one of these.
/// </summary>
public enum EngineThread : byte
{
    /// <summary>
    /// The process's first thread, which the OS gives its window and events to: it pumps them, and hands the game
    /// thread the input they make.
    /// </summary>
    Platform,

    /// <summary>
    /// What the frames run on, and where the ECS and the gems' hooks but Render are.
    /// </summary>
    Game,

    /// <summary>
    /// What the gems' Render hooks run on, at the GPU's pace, and whose the GPU is.
    /// </summary>
    Render,
}
