namespace Magic.Projects;

/// <summary>
/// A plugin's entry point: the one public class in a gem's assembly, named <c>EntryPoint</c>.
/// </summary>
/// <remarks>
/// <para>
/// Magic makes it by reflection after the gems whose parts its constructor takes and the gems its code references,
/// else in the order that the project lists the gems. Magic disposes the gems in reverse order.
/// </para>
/// <para>
/// It makes its parts with <c>new</c> and exports each as a public property; it holds nothing else. A part that draws
/// implements a form of <see cref="Graphics.IRender"/>, which runs on the render thread at its own pace. Each other
/// interface of a part is what the gem provides to the gems made after it.
/// </para>
/// <para>
/// The hooks run each frame on the game thread, in this order over the gems. Implement the hooks the gem needs, and
/// let each call the parts.
/// </para>
/// </remarks>
public interface IGem
{
    /// <summary>
    /// Does the game's work, one time a frame.
    /// </summary>
    void Update(in Frame frame) { }

    /// <summary>
    /// Does deterministic work that does not depend on the frame rate, such as physics.
    /// </summary>
    /// <remarks>
    /// It runs after <see cref="Update"/>, as many times a frame as the clock owes, up to its limit. Each run gets one
    /// step's clocks and no messages.
    /// </remarks>
    void FixedUpdate(in Frame frame) { }

    /// <summary>
    /// Does what follows from the frame's work: transforms, listeners, and what goes back to where it came from.
    /// </summary>
    void LateUpdate(in Frame frame) { }
}
