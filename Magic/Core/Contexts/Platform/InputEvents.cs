using System.Numerics;

namespace Magic.Contexts.Input;

// Input as it happens, published by the input gem and read from Frame.Events. Window is an IWindow.Handle.

/// <summary>
/// A key went down (<see cref="Repeat"/> when held long enough to repeat) or up.
/// </summary>
public readonly record struct KeyEvent(uint Window, Key Key, KeyModifiers Modifiers, bool Down, bool Repeat = false);

/// <summary>
/// What was typed, after the keyboard layout and any input method.
/// </summary>
public readonly record struct TextInput(uint Window, string Text);

/// <summary>
/// The mouse moved: <see cref="Position"/> in window points, top left (0, 0).
/// </summary>
public readonly record struct MouseMoved(uint Window, Vector2 Position);

/// <summary>
/// A mouse button went down (<see cref="Down"/>) or up.
/// </summary>
public readonly record struct MouseButtonEvent(uint Window, MouseButton Button, bool Down);

/// <summary>
/// The wheel turned: +Y away from the user, +X to the right.
/// </summary>
public readonly record struct MouseWheel(uint Window, Vector2 Delta);

/// <summary>
/// A window got (<see cref="Gained"/>) or lost the keyboard focus.
/// </summary>
public readonly record struct WindowFocus(uint Window, bool Gained);
