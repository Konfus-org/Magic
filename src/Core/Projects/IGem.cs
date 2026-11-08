using Magic.Rendering;

namespace Magic.Projects;

/// <summary>
/// A plugin: the one class in a gem's assembly. Magic makes it after what its constructor takes, in dependency order, then
/// in the order the project lists the gems, and disposes it in reverse. The hooks run every frame, in this order over the
/// gems: implement the ones the gem needs. <see cref="Render"/> runs on the render thread, at its own pace, and the
/// others on the game thread. Every other interface it implements is what it provides to
/// the gems made after it.
/// </summary>
public interface IGem
{
    /// <summary>
    /// Once a frame, for the game's work.
    /// </summary>
    void Update(in Frame frame) { }

    /// <summary>
    /// Work that is deterministic, independent of the frame rate, such as physics: after <see cref="Update"/>, as many
    /// times a frame as the clock owes, up to its limit, each with one step's clocks and no messages.
    /// </summary>
    void FixedUpdate(in Frame frame) { }

    /// <summary>
    /// What follows from the frame's work: transforms, listeners, what goes back to where it came from.
    /// </summary>
    void LateUpdate(in Frame frame) { }

    /// <summary>
    /// Drawing the frame, on the render thread, from what <see cref="LateUpdate"/> last handed it: as often as the GPU
    /// draws, more or less often than the game thread's frames. The gem records its passes into
    /// <paramref name="graph"/>, the frame every gem draws into, each in its <see cref="RenderLayer"/>; once every gem
    /// has, the GPU provider presents it. No messages.
    /// </summary>
    void Render(in Frame frame, RenderGraph graph) { }
}
