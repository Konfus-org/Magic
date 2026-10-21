using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.Contexts.Input;

/// <summary>
/// Every control Magic knows, on every kind of device, read through <see cref="IInput"/>. Each
/// <see cref="DeviceKind"/> has a block of 256 values, so a control's value says its device kind and its number
/// within it (<see cref="ControlExtensions"/>). The names are what an input map binds to in data: "KeySpace",
/// "GamepadSouth".
/// </summary>
public enum Control
{
    // Keyboard: physical keys, named for where they sit on a US layout, whatever a layout prints on them, so WASD is
    // the same four keys on every keyboard.
    KeyA = 0x000,
    KeyB,
    KeyC,
    KeyD,
    KeyE,
    KeyF,
    KeyG,
    KeyH,
    KeyI,
    KeyJ,
    KeyK,
    KeyL,
    KeyM,
    KeyN,
    KeyO,
    KeyP,
    KeyQ,
    KeyR,
    KeyS,
    KeyT,
    KeyU,
    KeyV,
    KeyW,
    KeyX,
    KeyY,
    KeyZ,
    Key0,
    Key1,
    Key2,
    Key3,
    Key4,
    Key5,
    Key6,
    Key7,
    Key8,
    Key9,
    KeyEnter,
    KeyEscape,
    KeyBackspace,
    KeyTab,
    KeySpace,
    KeyMinus,
    KeyEquals,
    KeyLeftBracket,
    KeyRightBracket,
    KeyBackslash,
    KeySemicolon,
    KeyApostrophe,

    /// <summary>
    /// The key left of 1: ` and ~ on a US layout.
    /// </summary>
    KeyGrave,
    KeyComma,
    KeyPeriod,
    KeySlash,

    /// <summary>
    /// The key beside left Shift on an ISO keyboard.
    /// </summary>
    KeyNonUsBackslash,

    /// <summary>
    /// The key beside Enter on an ISO keyboard.
    /// </summary>
    KeyNonUsHash,
    KeyCapsLock,
    KeyF1,
    KeyF2,
    KeyF3,
    KeyF4,
    KeyF5,
    KeyF6,
    KeyF7,
    KeyF8,
    KeyF9,
    KeyF10,
    KeyF11,
    KeyF12,
    KeyF13,
    KeyF14,
    KeyF15,
    KeyF16,
    KeyF17,
    KeyF18,
    KeyF19,
    KeyF20,
    KeyF21,
    KeyF22,
    KeyF23,
    KeyF24,
    KeyPrintScreen,
    KeyScrollLock,
    KeyPause,
    KeyInsert,
    KeyDelete,
    KeyHome,
    KeyEnd,
    KeyPageUp,
    KeyPageDown,
    KeyLeft,
    KeyRight,
    KeyUp,
    KeyDown,
    KeyNumLock,
    KeyNumpad0,
    KeyNumpad1,
    KeyNumpad2,
    KeyNumpad3,
    KeyNumpad4,
    KeyNumpad5,
    KeyNumpad6,
    KeyNumpad7,
    KeyNumpad8,
    KeyNumpad9,
    KeyNumpadDivide,
    KeyNumpadMultiply,
    KeyNumpadMinus,
    KeyNumpadPlus,
    KeyNumpadEnter,
    KeyNumpadPeriod,
    KeyNumpadEquals,

    /// <summary>
    /// The context menu key.
    /// </summary>
    KeyMenu,
    KeyLeftCtrl,
    KeyLeftShift,
    KeyLeftAlt,

    /// <summary>
    /// The Windows key, or Command on a Mac.
    /// </summary>
    KeyLeftSuper,
    KeyRightCtrl,
    KeyRightShift,
    KeyRightAlt,

    /// <summary>
    /// The Windows key, or Command on a Mac.
    /// </summary>
    KeyRightSuper,

    // Mouse.
    MouseLeft = 0x100,
    MouseMiddle,
    MouseRight,
    MouseBack,
    MouseForward,

    /// <summary>
    /// Where the pointer is across the window, in pixels from its left edge.
    /// </summary>
    MouseX,

    /// <summary>
    /// Where the pointer is down the window, in pixels from its top edge.
    /// </summary>
    MouseY,

    /// <summary>
    /// How far the mouse moved right this frame, in pixels.
    /// </summary>
    MouseMotionX,

    /// <summary>
    /// How far the mouse moved down this frame, in pixels.
    /// </summary>
    MouseMotionY,

    /// <summary>
    /// How far the wheel scrolled right this frame, in notches.
    /// </summary>
    MouseWheelX,

    /// <summary>
    /// How far the wheel scrolled away from the player this frame, in notches.
    /// </summary>
    MouseWheelY,

    // Gamepad: buttons by position, the same on every pad whatever it prints on them.

    /// <summary>
    /// The bottom face button: Xbox A, PlayStation Cross, Nintendo B.
    /// </summary>
    GamepadSouth = 0x200,

    /// <summary>
    /// The right face button: Xbox B, PlayStation Circle, Nintendo A.
    /// </summary>
    GamepadEast,

    /// <summary>
    /// The left face button: Xbox X, PlayStation Square, Nintendo Y.
    /// </summary>
    GamepadWest,

    /// <summary>
    /// The top face button: Xbox Y, PlayStation Triangle, Nintendo X.
    /// </summary>
    GamepadNorth,
    GamepadBack,
    GamepadGuide,
    GamepadStart,

    /// <summary>
    /// Pressing the left stick in.
    /// </summary>
    GamepadLeftStick,

    /// <summary>
    /// Pressing the right stick in.
    /// </summary>
    GamepadRightStick,
    GamepadLeftShoulder,
    GamepadRightShoulder,
    GamepadDPadUp,
    GamepadDPadDown,
    GamepadDPadLeft,
    GamepadDPadRight,

    /// <summary>
    /// An extra button some pads have: Xbox Share, PlayStation microphone, Nintendo Capture.
    /// </summary>
    GamepadMisc1,
    GamepadMisc2,
    GamepadMisc3,
    GamepadMisc4,
    GamepadMisc5,
    GamepadMisc6,
    GamepadLeftPaddle1,
    GamepadLeftPaddle2,
    GamepadRightPaddle1,
    GamepadRightPaddle2,

    /// <summary>
    /// Pressing a PlayStation pad's touchpad.
    /// </summary>
    GamepadTouchpad,

    /// <summary>
    /// The left stick, -1 left to 1 right.
    /// </summary>
    GamepadLeftX,

    /// <summary>
    /// The left stick, -1 down to 1 up.
    /// </summary>
    GamepadLeftY,

    /// <summary>
    /// The right stick, -1 left to 1 right.
    /// </summary>
    GamepadRightX,

    /// <summary>
    /// The right stick, -1 down to 1 up.
    /// </summary>
    GamepadRightY,

    /// <summary>
    /// The left trigger, 0 released to 1 fully pressed.
    /// </summary>
    GamepadLeftTrigger,

    /// <summary>
    /// The right trigger, 0 released to 1 fully pressed.
    /// </summary>
    GamepadRightTrigger,
}

/// <summary>
/// One control this frame. Every control has a value: a key or button is 0 or 1, a trigger 0 to 1, a stick axis -1
/// to 1, and the mouse's position, motion and wheel are pixels and notches. A key, button, trigger or stick is down
/// while its value is at least half way, and pressed or released when that changed this frame, so a tap that starts
/// and ends between two frames still shows as both.
/// </summary>
/// <param name="Value">How far, how much, or where.</param>
/// <param name="IsDown">Held now.</param>
/// <param name="WasPressed">Went down this frame.</param>
/// <param name="WasReleased">Came up this frame.</param>
public readonly record struct ControlState(float Value, bool IsDown, bool WasPressed, bool WasReleased);
