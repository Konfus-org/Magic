namespace Magic.Input;

/// <summary>
/// Reads every input device that the player uses in one way.
/// </summary>
/// <remarks>
/// <para>
/// A device is a set of controls. Every control has a value and is down, pressed or released like a button. Thus a
/// key, a trigger and a stick axis read the same, and an action can bind to any of them by name.
/// </para>
/// <para>
/// The provider updates it in its <see cref="Projects.IGem.Update"/>, before the gems that use it, because they are
/// made after it. It does not change for the rest of the frame. Read it only on the game thread, in Update, FixedUpdate
/// or LateUpdate. Copy what Render needs.
/// </para>
/// </remarks>
public interface IInput
{
    /// <summary>
    /// The devices connected now, in the order they connected.
    /// </summary>
    ReadOnlySpan<InputDevice> Devices { get; }

    /// <summary>
    /// The characters typed in this frame, in order, for a text field.
    /// </summary>
    /// <remarks>
    /// The keyboard layout and the input method make these characters.
    /// </remarks>
    string Text { get; }

    /// <summary>
    /// The state of a control, combined over every device that has it.
    /// </summary>
    /// <remarks>
    /// It is down while it is down on any device. Its value is the one with the largest magnitude.
    /// </remarks>
    ControlState this[Control control] { get; }

    /// <summary>
    /// The state of a control on one device, or all zero if the device is gone or does not have the control.
    /// </summary>
    ControlState this[InputDevice device, Control control] { get; }
}
