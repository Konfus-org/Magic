namespace Magic.Contexts.Frames;

/// <summary>
/// The parts of a frame, in the order they run. Every gem's hook for one phase runs before any gem's hook for the
/// next, so data flows one way from the start of a frame to its end. The first four run on the main thread; Render
/// runs on the render thread, alongside the next frame's Update, FixedUpdate and LateUpdate.
/// </summary>
public enum Phase
{
    /// <summary>
    /// Taking in what happened since the last frame (input, commands, finished loads) and moving the world on:
    /// gameplay. Main thread.
    /// </summary>
    Update,

    /// <summary>
    /// Moving the world on by fixed steps, as many as fit in the world's time since the last frame, so none while it
    /// is paused: simulation. Main thread.
    /// </summary>
    FixedUpdate,

    /// <summary>
    /// Preparing what Render will read, into what Render is not reading, as the render thread may still be drawing the
    /// frame before. Main thread.
    /// </summary>
    LateUpdate,

    /// <summary>
    /// The one point where both engine threads hold still, once the render thread has finished the frame before:
    /// swapping what LateUpdate prepared in for Render to read, and nothing more, as every gem waits on it. The
    /// settings changed since the last frame are applied just before. Main thread.
    /// </summary>
    Sync,

    /// <summary>
    /// Drawing what Sync swapped in, submitting and presenting it, and taking in the OS's events. Render thread, from
    /// the last gem made to the first.
    /// </summary>
    Render,
}
