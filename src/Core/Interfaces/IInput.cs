using Magic.Contexts.Frames;
using Magic.Contexts.Input;

namespace Magic.Interfaces;

/// <summary>
/// Every input device the player uses, read one way. A device is a set of controls, and every control has a value and
/// is down, pressed or released like a button, so a key, a trigger and a stick axis are read alike and an action can
/// be bound to any of them by name. The provider updates it in its <see cref="Phase.Update"/>, before the gems that
/// take it; it stays fixed for the rest of the frame. The main thread's: read it in Update, FixedUpdate or LateUpdate,
/// and copy what Render needs.
/// </summary>
public interface IInput
{
    /// <summary>
    /// The devices connected now, in the order they connected.
    /// </summary>
    ReadOnlySpan<InputDevice> Devices { get; }

    /// <summary>
    /// What was typed this frame, in order, as the keyboard layout and input method made it: characters rather than
    /// keys, for a text field. Empty when nothing was.
    /// </summary>
    string Text { get; }

    /// <summary>
    /// A control on every device that has it, combined: down while down on any, with the value of largest magnitude.
    /// </summary>
    ControlState this[Control control] { get; }

    /// <summary>
    /// A control on one device; all zero for a device that is gone or does not have it.
    /// </summary>
    ControlState this[InputDevice device, Control control] { get; }
}
