namespace Magic.Contexts.Frames;

/// <summary>
/// The parts of a frame, in the order they run. Every gem's hook for one phase runs before any gem's hook for the
/// next, so data flows one way from the start of a frame to its end. The first three run on the main thread; Render
/// runs on the render thread, alongside the next frame's Update and FixedUpdate.
/// </summary>
public enum Phase
{
    /// <summary>
    /// Taking in what happened since the last frame (input, commands, finished loads) and moving the world on:
    /// gameplay. Main thread.
    /// </summary>
    Update,

    /// <summary>
    /// Moving the world on by fixed steps, as many as fit in the time since the last frame: simulation. Main thread.
    /// </summary>
    FixedUpdate,

    /// <summary>
    /// Copying what Render will read: the one point where both engine threads hold still, since the render thread has
    /// finished the frame before. Main thread.
    /// </summary>
    LateUpdate,

    /// <summary>
    /// Drawing what LateUpdate copied, submitting and presenting it, and taking in the OS's events. Render thread, from
    /// the last gem made to the first.
    /// </summary>
    Render,
}
