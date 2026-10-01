namespace Magic.Contexts.Input;

/// <summary>A key, by what it means on the current keyboard layout (so <see cref="A"/> is the key that types a).</summary>
public enum Key
{
    Unknown,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Escape, Enter, KeypadEnter, Tab, Backspace, Space, Grave,
    Insert, Delete, Home, End, PageUp, PageDown,
    Left, Right, Up, Down,
    LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt, LeftSuper, RightSuper,
}

[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Super = 8,
}

public enum MouseButton : byte
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>Gamepad buttons by position, not label: <see cref="South"/> is A on Xbox, Cross on PlayStation.</summary>
public enum GamepadButton
{
    South,
    East,
    West,
    North,
    Back,
    Guide,
    Start,
    LeftStick,
    RightStick,
    LeftShoulder,
    RightShoulder,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
}

/// <summary>Sticks run -1 to 1 (+X right, +Y down), triggers 0 to 1.</summary>
public enum GamepadAxis
{
    LeftX,
    LeftY,
    RightX,
    RightY,
    LeftTrigger,
    RightTrigger,
}
