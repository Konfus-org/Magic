namespace Magic.Projects;

/// <summary>
/// A plugin: the one class in a gem's assembly. Magic makes it after the gems its constructor takes and its code
/// references, then in the order the project lists the gems, and disposes it in reverse. The hooks run every frame, in
/// this order over the gems, on the game thread: implement the ones the gem needs. A gem that draws implements one of
/// <see cref="Graphics.IRender"/>'s forms, which runs on the render thread at its own pace. Every other interface it
/// implements is what it provides to the gems made after it.
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
}
