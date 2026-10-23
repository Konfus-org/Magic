using Magic.Contexts.ECS;
using Magic.Contexts.Frames;

namespace Magic.Interfaces;

/// <summary>
/// A script that runs every frame for its entity (<see cref="IScript"/>): its hooks run on the main thread, in the
/// phase they are named for; implement the ones it needs, and the rest do nothing.
/// <para>
/// <see cref="FixedUpdate"/> runs every step. <see cref="Update"/> and <see cref="LateUpdate"/> run each frame near the
/// player, but whatever runs the scripts may call them less often for an entity far from the player's cameras (those
/// tagged <see cref="Tag.Player"/>), the frame's DeltaSeconds then the time since the last call, so what moves by it
/// moves as far.
/// </para>
/// <para>
/// While the world is paused (<see cref="Frame.IsPaused"/>), Update and LateUpdate wait, unless the class is
/// <see cref="Attributes.UnpausableAttribute">[Unpausable]</see> or the entity is tagged <see cref="Tag.Unpausable"/>,
/// and FixedUpdate never runs, as there are no fixed steps.
/// </para>
/// </summary>
public interface IBehavior : IScript
{
    /// <summary>
    /// <see cref="Phase.Update"/>: move the entity on.
    /// </summary>
    void Update(in Frame frame)
    {
    }

    /// <summary>
    /// <see cref="Phase.FixedUpdate"/>: move the entity on by one fixed step, as many times as there are steps since
    /// the last frame; <paramref name="frame"/>'s DeltaSeconds is the step.
    /// </summary>
    void FixedUpdate(in Frame frame)
    {
    }

    /// <summary>
    /// <see cref="Phase.LateUpdate"/>: what must see where everything ended up this frame, such as a camera following
    /// its target.
    /// </summary>
    void LateUpdate(in Frame frame)
    {
    }
}
