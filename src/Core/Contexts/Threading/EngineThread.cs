using Magic.Services;

namespace Magic.Contexts.Threading;

/// <summary>
/// The engine's own threads, which <see cref="Threads"/> hands work to. Everything else runs as tasks on .NET's
/// thread pool, or on a thread of its own when it runs as long as the run does (<see cref="Threads.RunAsync"/>).
/// </summary>
public enum EngineThread
{
    /// <summary>
    /// The engine has not yet started, or the thread is not one of its own.
    /// </summary>
    Unknown,

    /// <summary>
    /// The game: gems' Update, FixedUpdate and LateUpdate hooks, the ECS and its systems. The engine starts it once
    /// the gems are made, and it ends with the frame loop.
    /// </summary>
    Main,

    /// <summary>
    /// The process's first thread, which the OS ties windows and their events to: gem constructors and Dispose, the
    /// Render hooks, the window and the GPU. It runs a frame's Render while the main thread moves on to the next
    /// frame.
    /// </summary>
    Render,
}
