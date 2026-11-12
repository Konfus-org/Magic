namespace Magic.Threading;

/// <summary>
/// The engine's own threads, which each run for the whole run, named for their member in a profiler or debugger.
/// </summary>
/// <remarks>
/// Background work is a task, not one of these.
/// </remarks>
public enum EngineThread : byte
{
    /// <summary>
    /// The process's first thread, which the OS gives its window and events to: it pumps them, and hands the game
    /// thread the input they make.
    /// </summary>
    Platform,

    /// <summary>
    /// Where the frames, the ECS and every gem hook except Render run.
    /// </summary>
    Game,

    /// <summary>
    /// What the gems' Render hooks run on, at the GPU's pace, and whose the GPU is.
    /// </summary>
    Render,
}
