using Magic.Contexts.Input;
using Magic.Interfaces;
using static Magic.ImGuiDebugUI.ImGui;
using static Magic.ImGuiDebugUI.ImGuiMouseButton_;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// The frame's input as ImGui takes it: each key Magic knows as ImGui's key, the mouse in window pixels, and what was
/// typed. ImGui drops a key event that says what it already knows, so every key is told every frame.
/// </summary>
internal static unsafe class ImGuiInput
{
    private static readonly (Control Control, int Key)[] _keys = CreateKeys();

    internal static void Feed(ImGuiIO_t* io, IInput input, TextArena text)
    {
        foreach ((Control control, int key) in _keys)
        {
            FeedKey(io, key, input[control]);
        }

        // The modifiers too, which ImGui reads shortcuts by, each held by the key on either side.
        ImGuiIO_AddKeyEvent(
            io,
            ImGuiMod_Ctrl,
            input[Control.KeyLeftCtrl].IsDown || input[Control.KeyRightCtrl].IsDown);
        ImGuiIO_AddKeyEvent(
            io,
            ImGuiMod_Shift,
            input[Control.KeyLeftShift].IsDown || input[Control.KeyRightShift].IsDown);
        ImGuiIO_AddKeyEvent(
            io,
            ImGuiMod_Alt,
            input[Control.KeyLeftAlt].IsDown || input[Control.KeyRightAlt].IsDown);
        ImGuiIO_AddKeyEvent(
            io,
            ImGuiMod_Super,
            input[Control.KeyLeftSuper].IsDown || input[Control.KeyRightSuper].IsDown);

        ImGuiIO_AddMousePosEvent(io, input[Control.MouseX].Value, input[Control.MouseY].Value);
        FeedButton(io, (int)ImGuiMouseButton_Left, input[Control.MouseLeft]);
        FeedButton(io, (int)ImGuiMouseButton_Right, input[Control.MouseRight]);
        FeedButton(io, (int)ImGuiMouseButton_Middle, input[Control.MouseMiddle]);

        // ImGui scrolls right for a negative x.
        float wheelX = input[Control.MouseWheelX].Value;
        float wheelY = input[Control.MouseWheelY].Value;
        if (wheelX != 0 || wheelY != 0) ImGuiIO_AddMouseWheelEvent(io, -wheelX, wheelY);

        // Only while a field has the keyboard: the key that opens a window must not be typed into the field it opens.
        if (io->WantTextInput && input.Text.Length > 0) ImGuiIO_AddInputCharactersUTF8(io, text.Add(input.Text));
    }

    /// <summary>
    /// A key's state, with a tap that went down and up between two frames told as both.
    /// </summary>
    private static void FeedKey(ImGuiIO_t* io, int key, ControlState state)
    {
        if (state.WasPressed && !state.IsDown) ImGuiIO_AddKeyEvent(io, key, true);

        ImGuiIO_AddKeyEvent(io, key, state.IsDown);
    }

    private static void FeedButton(ImGuiIO_t* io, int button, ControlState state)
    {
        if (state.WasPressed && !state.IsDown) ImGuiIO_AddMouseButtonEvent(io, button, true);

        ImGuiIO_AddMouseButtonEvent(io, button, state.IsDown);
    }

    /// <summary>
    /// Each keyboard control beside the ImGui key it is: the runs both number alike, then the rest one by one.
    /// </summary>
    private static (Control Control, int Key)[] CreateKeys()
    {
        List<(Control Control, int Key)> keys =
        [
            (Control.KeyEnter, ImGuiKey_Enter),
            (Control.KeyEscape, ImGuiKey_Escape),
            (Control.KeyBackspace, ImGuiKey_Backspace),
            (Control.KeyTab, ImGuiKey_Tab),
            (Control.KeySpace, ImGuiKey_Space),
            (Control.KeyMinus, ImGuiKey_Minus),
            (Control.KeyEquals, ImGuiKey_Equal),
            (Control.KeyLeftBracket, ImGuiKey_LeftBracket),
            (Control.KeyRightBracket, ImGuiKey_RightBracket),
            (Control.KeyBackslash, ImGuiKey_Backslash),
            (Control.KeySemicolon, ImGuiKey_Semicolon),
            (Control.KeyApostrophe, ImGuiKey_Apostrophe),
            (Control.KeyGrave, ImGuiKey_GraveAccent),
            (Control.KeyComma, ImGuiKey_Comma),
            (Control.KeyPeriod, ImGuiKey_Period),
            (Control.KeySlash, ImGuiKey_Slash),
            (Control.KeyNonUsBackslash, ImGuiKey_Oem102),
            (Control.KeyCapsLock, ImGuiKey_CapsLock),
            (Control.KeyPrintScreen, ImGuiKey_PrintScreen),
            (Control.KeyScrollLock, ImGuiKey_ScrollLock),
            (Control.KeyPause, ImGuiKey_Pause),
            (Control.KeyInsert, ImGuiKey_Insert),
            (Control.KeyDelete, ImGuiKey_Delete),
            (Control.KeyHome, ImGuiKey_Home),
            (Control.KeyEnd, ImGuiKey_End),
            (Control.KeyPageUp, ImGuiKey_PageUp),
            (Control.KeyPageDown, ImGuiKey_PageDown),
            (Control.KeyLeft, ImGuiKey_LeftArrow),
            (Control.KeyRight, ImGuiKey_RightArrow),
            (Control.KeyUp, ImGuiKey_UpArrow),
            (Control.KeyDown, ImGuiKey_DownArrow),
            (Control.KeyNumLock, ImGuiKey_NumLock),
            (Control.KeyNumpadDivide, ImGuiKey_KeypadDivide),
            (Control.KeyNumpadMultiply, ImGuiKey_KeypadMultiply),
            (Control.KeyNumpadMinus, ImGuiKey_KeypadSubtract),
            (Control.KeyNumpadPlus, ImGuiKey_KeypadAdd),
            (Control.KeyNumpadEnter, ImGuiKey_KeypadEnter),
            (Control.KeyNumpadPeriod, ImGuiKey_KeypadDecimal),
            (Control.KeyNumpadEquals, ImGuiKey_KeypadEqual),
            (Control.KeyMenu, ImGuiKey_Menu),
            (Control.KeyLeftCtrl, ImGuiKey_LeftCtrl),
            (Control.KeyLeftShift, ImGuiKey_LeftShift),
            (Control.KeyLeftAlt, ImGuiKey_LeftAlt),
            (Control.KeyLeftSuper, ImGuiKey_LeftSuper),
            (Control.KeyRightCtrl, ImGuiKey_RightCtrl),
            (Control.KeyRightShift, ImGuiKey_RightShift),
            (Control.KeyRightAlt, ImGuiKey_RightAlt),
            (Control.KeyRightSuper, ImGuiKey_RightSuper),
        ];
        AddRun(keys, Control.KeyA, ImGuiKey_A, 26);
        AddRun(keys, Control.Key0, ImGuiKey_0, 10);
        AddRun(keys, Control.KeyNumpad0, ImGuiKey_Keypad0, 10);
        AddRun(keys, Control.KeyF1, ImGuiKey_F1, 24);

        return [.. keys];
    }

    private static void AddRun(List<(Control Control, int Key)> keys, Control control, int key, int count)
    {
        for (int i = 0; i < count; i++)
        {
            keys.Add((control + i, key + i));
        }
    }
}
