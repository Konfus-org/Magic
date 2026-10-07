namespace Magic.Interfaces;

/// <summary>
/// The phases of a frame, in order: the <see cref="IGem"/> hook a system runs in (<see cref="ISystem.Phase"/>). Everything that produces
/// frame state (movement, animation, the transform hierarchy) runs by <see cref="LateUpdate"/>; <see cref="Render"/>
/// consumes it.
/// </summary>
public enum UpdateType
{
    Update,
    FixedUpdate,
    LateUpdate,
    Render,

    /// <summary>
    /// What draws on top of the scene: debug text, the screen log, UI. Runs after every <see cref="Render"/> system.
    /// </summary>
    Overlay
}
