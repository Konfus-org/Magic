using Magic.Contexts.Input;
using Magic.Interfaces;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The frame's input as ImGui takes it: each key Magic knows as ImGui's key, the mouse in window pixels, and what was
/// typed. ImGui drops a key event that says what it already knows, so every key is told every frame.
/// </summary>
internal static unsafe class ImGuiInput
{
    private static readonly (Control Control, ImGuiKey Key)[] _keys = Keys();

    internal static void Feed(ImGuiIO* io, IInput input, TextArena text)
    {
        foreach ((Control control, ImGuiKey key) in _keys)
        {
            FeedKey(io, key, input[control]);
        }

        ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiModCtrl, input[Control.KeyLeftCtrl].IsDown || input[Control.KeyRightCtrl].IsDown);
        ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiModShift, input[Control.KeyLeftShift].IsDown || input[Control.KeyRightShift].IsDown);
        ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiModAlt, input[Control.KeyLeftAlt].IsDown || input[Control.KeyRightAlt].IsDown);
        ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiModSuper, input[Control.KeyLeftSuper].IsDown || input[Control.KeyRightSuper].IsDown);

        ImGuiIO.AddMousePosEvent(io, input[Control.MouseX].Value, input[Control.MouseY].Value);
        FeedButton(io, (int)ImGuiMouseButton.Left, input[Control.MouseLeft]);
        FeedButton(io, (int)ImGuiMouseButton.Right, input[Control.MouseRight]);
        FeedButton(io, (int)ImGuiMouseButton.Middle, input[Control.MouseMiddle]);

        // ImGui scrolls right for a negative x.
        float wheelX = input[Control.MouseWheelX].Value;
        float wheelY = input[Control.MouseWheelY].Value;
        if (wheelX != 0 || wheelY != 0) ImGuiIO.AddMouseWheelEvent(io, -wheelX, wheelY);

        // Only while a field has the keyboard: the key that opens a window must not be typed into the field it opens.
        if (io->WantTextInput && input.Text.Length > 0) ImGuiIO.AddInputCharactersUTF8(io, text.Of(input.Text));
    }

    /// <summary>
    /// A key's state, with a tap that went down and up between two frames told as both.
    /// </summary>
    private static void FeedKey(ImGuiIO* io, ImGuiKey key, ControlState state)
    {
        if (state.WasPressed && !state.IsDown) ImGuiIO.AddKeyEvent(io, key, true);

        ImGuiIO.AddKeyEvent(io, key, state.IsDown);
    }

    private static void FeedButton(ImGuiIO* io, int button, ControlState state)
    {
        if (state.WasPressed && !state.IsDown) ImGuiIO.AddMouseButtonEvent(io, button, true);

        ImGuiIO.AddMouseButtonEvent(io, button, state.IsDown);
    }

    /// <summary>
    /// Each keyboard control beside the ImGui key it is: the runs both number alike, then the rest one by one.
    /// </summary>
    private static (Control Control, ImGuiKey Key)[] Keys()
    {
        List<(Control Control, ImGuiKey Key)> keys =
        [
            (Control.KeyEnter, ImGuiKey.Enter),
            (Control.KeyEscape, ImGuiKey.Escape),
            (Control.KeyBackspace, ImGuiKey.Backspace),
            (Control.KeyTab, ImGuiKey.Tab),
            (Control.KeySpace, ImGuiKey.Space),
            (Control.KeyMinus, ImGuiKey.Minus),
            (Control.KeyEquals, ImGuiKey.Equal),
            (Control.KeyLeftBracket, ImGuiKey.LeftBracket),
            (Control.KeyRightBracket, ImGuiKey.RightBracket),
            (Control.KeyBackslash, ImGuiKey.Backslash),
            (Control.KeySemicolon, ImGuiKey.Semicolon),
            (Control.KeyApostrophe, ImGuiKey.Apostrophe),
            (Control.KeyGrave, ImGuiKey.GraveAccent),
            (Control.KeyComma, ImGuiKey.Comma),
            (Control.KeyPeriod, ImGuiKey.Period),
            (Control.KeySlash, ImGuiKey.Slash),
            (Control.KeyNonUsBackslash, ImGuiKey.Oem102),
            (Control.KeyCapsLock, ImGuiKey.CapsLock),
            (Control.KeyPrintScreen, ImGuiKey.PrintScreen),
            (Control.KeyScrollLock, ImGuiKey.ScrollLock),
            (Control.KeyPause, ImGuiKey.Pause),
            (Control.KeyInsert, ImGuiKey.Insert),
            (Control.KeyDelete, ImGuiKey.Delete),
            (Control.KeyHome, ImGuiKey.Home),
            (Control.KeyEnd, ImGuiKey.End),
            (Control.KeyPageUp, ImGuiKey.PageUp),
            (Control.KeyPageDown, ImGuiKey.PageDown),
            (Control.KeyLeft, ImGuiKey.LeftArrow),
            (Control.KeyRight, ImGuiKey.RightArrow),
            (Control.KeyUp, ImGuiKey.UpArrow),
            (Control.KeyDown, ImGuiKey.DownArrow),
            (Control.KeyNumLock, ImGuiKey.NumLock),
            (Control.KeyNumpadDivide, ImGuiKey.KeypadDivide),
            (Control.KeyNumpadMultiply, ImGuiKey.KeypadMultiply),
            (Control.KeyNumpadMinus, ImGuiKey.KeypadSubtract),
            (Control.KeyNumpadPlus, ImGuiKey.KeypadAdd),
            (Control.KeyNumpadEnter, ImGuiKey.KeypadEnter),
            (Control.KeyNumpadPeriod, ImGuiKey.KeypadDecimal),
            (Control.KeyNumpadEquals, ImGuiKey.KeypadEqual),
            (Control.KeyMenu, ImGuiKey.Menu),
            (Control.KeyLeftCtrl, ImGuiKey.LeftCtrl),
            (Control.KeyLeftShift, ImGuiKey.LeftShift),
            (Control.KeyLeftAlt, ImGuiKey.LeftAlt),
            (Control.KeyLeftSuper, ImGuiKey.LeftSuper),
            (Control.KeyRightCtrl, ImGuiKey.RightCtrl),
            (Control.KeyRightShift, ImGuiKey.RightShift),
            (Control.KeyRightAlt, ImGuiKey.RightAlt),
            (Control.KeyRightSuper, ImGuiKey.RightSuper),
        ];
        AddRun(keys, Control.KeyA, ImGuiKey.A, 26);
        AddRun(keys, Control.Key0, ImGuiKey.Key0, 10);
        AddRun(keys, Control.KeyNumpad0, ImGuiKey.Keypad0, 10);
        AddRun(keys, Control.KeyF1, ImGuiKey.F1, 24);
        return [.. keys];
    }

    private static void AddRun(List<(Control Control, ImGuiKey Key)> keys, Control control, ImGuiKey key, int count)
    {
        for (int i = 0; i < count; i++)
        {
            keys.Add((control + i, key + i));
        }
    }
}
