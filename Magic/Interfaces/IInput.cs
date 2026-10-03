using Magic.Contexts.Input;
using System.Numerics;

namespace Magic.Interfaces;

/// <summary>
/// Keyboard, mouse and gamepad state, provided by an input gem. It changes once a frame, in the input gem's
/// <see cref="IGem.Update"/>, and holds still until the next: Pressed and Released mean "since the
/// last frame", and a press and release inside one frame still reads as both. Gamepads are numbered from 0 in the
/// order they connect; a number stays with its gamepad until it disconnects, and an unconnected one reads as idle.
/// </summary>
public interface IInput
{
    /// <summary>
    /// Where the mouse is, in points of the window it is over (<see cref="IWindow.Size"/>), top left (0, 0).
    /// </summary>
    Vector2 MousePosition { get; }

    /// <summary>
    /// How far the mouse moved since the last frame, in points.
    /// </summary>
    Vector2 MouseDelta { get; }

    /// <summary>
    /// Wheel movement since the last frame: +Y away from the user, +X to the right.
    /// </summary>
    Vector2 MouseWheel { get; }

    bool IsDown(Key key);

    bool WasPressed(Key key);

    bool WasReleased(Key key);

    bool IsDown(MouseButton button);

    bool WasPressed(MouseButton button);

    bool WasReleased(MouseButton button);

    bool IsConnected(int gamepad);

    bool IsDown(int gamepad, GamepadButton button);

    bool WasPressed(int gamepad, GamepadButton button);

    bool WasReleased(int gamepad, GamepadButton button);

    /// <summary>
    /// Raw, no dead zone: see <see cref="GamepadAxis"/> for the ranges.
    /// </summary>
    float Axis(int gamepad, GamepadAxis axis);
}
