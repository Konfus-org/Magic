using Magic.Services;

namespace Magic.Contexts;

/// <summary>
/// Names a thread to <see cref="Threads"/>: <see cref="Worker"/>, <see cref="Main"/>, <see cref="Render"/> or one
/// made by <see cref="Threads.Create"/>.
/// </summary>
public readonly record struct ThreadId(int Value)
{
    /// <summary>
    /// No one thread: whichever worker is free first, which is .NET's own thread pool.
    /// </summary>
    public static ThreadId Worker => default;

    /// <summary>
    /// The simulation thread: the frame loop's, where the ECS, scripts and the debug UI run.
    /// </summary>
    public static ThreadId Main => new(1);

    /// <summary>
    /// The thread that draws and owns the windows: the one the process started on, as the OS wants.
    /// </summary>
    public static ThreadId Render => new(2);
}
