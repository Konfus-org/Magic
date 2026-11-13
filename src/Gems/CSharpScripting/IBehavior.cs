using Magic.Projects;

namespace Magic.CSharpScripting;

/// <summary>
/// A script that does work for its entity each frame, in the hooks of the frame.
/// </summary>
/// <remarks>
/// The hooks run on the game thread, in the order that the behaviours started. Implement only the hooks that the
/// behaviour needs. If a hook throws, the error is logged and the behaviour ends.
/// </remarks>
public interface IBehavior : IScript
{
    /// <summary>
    /// Moves the entity on, one time a frame.
    /// </summary>
    void Update(in Frame frame) { }

    /// <summary>
    /// Moves the entity on by one fixed step, as many times a frame as the clock owes.
    /// </summary>
    void FixedUpdate(in Frame frame) { }

    /// <summary>
    /// Does the work that must see where everything is at the end of the frame, such as a camera that follows a target.
    /// </summary>
    void LateUpdate(in Frame frame) { }
}
