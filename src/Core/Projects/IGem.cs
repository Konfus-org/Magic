namespace Magic.Projects;

/// <summary>
/// A plugin's entry point: the one class in a gem's assembly, named <c>EntryPoint</c>. Magic makes it after the gems
/// whose parts its constructor takes and the gems its code references, then in the order the project lists the gems,
/// and disposes it in reverse. It is public because nothing in its assembly makes it: Magic does, by reflection. It makes its parts with <c>new</c> and exports each as a public property; it holds
/// nothing else. Magic sets up each part by the interfaces it implements: a part that draws implements one of
/// <see cref="Graphics.IRender"/>'s forms, which runs on the render thread at its own pace, and each other interface is
/// what the gem provides to the gems made after it. The hooks run every frame, in this order over the gems, on the
/// game thread: implement the ones the gem needs, and have each call its parts.
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
