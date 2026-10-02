using Magic.Contexts.Input;
using SDL3;

namespace SDLInputGem;

/// <summary>SDL's key codes and modifiers as the engine's.</summary>
internal static class SdlKeyExtensions
{
    extension(SDL.Keycode key)
    {
        public Key ToKey() => key switch
        {    
            >= SDL.Keycode.A and <= SDL.Keycode.Z => Key.A + (int)(key - SDL.Keycode.A),
            >= SDL.Keycode.Alpha0 and <= SDL.Keycode.Alpha9 => Key.D0 + (int)(key - SDL.Keycode.Alpha0),
            >= SDL.Keycode.F1 and <= SDL.Keycode.F12 => Key.F1 + (int)(key - SDL.Keycode.F1),
            SDL.Keycode.Escape => Key.Escape,
            SDL.Keycode.Return => Key.Enter,
            SDL.Keycode.KpEnter => Key.KeypadEnter,
            SDL.Keycode.Tab => Key.Tab,
            SDL.Keycode.Backspace => Key.Backspace,
            SDL.Keycode.Space => Key.Space,
            SDL.Keycode.Grave => Key.Grave,
            SDL.Keycode.Insert => Key.Insert,
            SDL.Keycode.Delete => Key.Delete,
            SDL.Keycode.Home => Key.Home,
            SDL.Keycode.End => Key.End,
            SDL.Keycode.Pageup => Key.PageUp,
            SDL.Keycode.Pagedown => Key.PageDown,
            SDL.Keycode.Left => Key.Left,
            SDL.Keycode.Right => Key.Right,
            SDL.Keycode.Up => Key.Up,
            SDL.Keycode.Down => Key.Down,
            SDL.Keycode.LShift => Key.LeftShift,
            SDL.Keycode.RShift => Key.RightShift,
            SDL.Keycode.LCtrl => Key.LeftCtrl,
            SDL.Keycode.RCtrl => Key.RightCtrl,
            SDL.Keycode.LAlt => Key.LeftAlt,
            SDL.Keycode.RAlt => Key.RightAlt,
            SDL.Keycode.LGUI => Key.LeftSuper,
            SDL.Keycode.RGUI => Key.RightSuper,
            _ => Key.Unknown,
        };
    }

    extension(SDL.Keymod mod)
    {
        public KeyModifiers ToModifiers()
        {    
            KeyModifiers modifiers = KeyModifiers.None;
    
            if ((mod & SDL.Keymod.Shift) != 0)
                modifiers |= KeyModifiers.Shift;
            if ((mod & SDL.Keymod.Ctrl) != 0)
                modifiers |= KeyModifiers.Ctrl;
            if ((mod & SDL.Keymod.Alt) != 0)
                modifiers |= KeyModifiers.Alt;
            if ((mod & SDL.Keymod.GUI) != 0)
                modifiers |= KeyModifiers.Super;
    
            return modifiers;
        }
    }
}
