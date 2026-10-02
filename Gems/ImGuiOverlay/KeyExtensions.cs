using Hexa.NET.ImGui;
using Magic.Contexts.Input;

namespace ImGuiOverlayGem;

internal static class KeyExtensions
{
    extension(Key key)
    {
        /// <summary>ImGui has its own key enum; these are the keys a text field needs to edit, select and copy. Characters arrive as text.</summary>
        public ImGuiKey ToImGuiKey()
        {
            return key switch
            {
                Key.Enter => ImGuiKey.Enter,
                Key.KeypadEnter => ImGuiKey.KeypadEnter,
                Key.Backspace => ImGuiKey.Backspace,
                Key.Delete => ImGuiKey.Delete,
                Key.Left => ImGuiKey.LeftArrow,
                Key.Right => ImGuiKey.RightArrow,
                Key.Home => ImGuiKey.Home,
                Key.End => ImGuiKey.End,
                Key.Up => ImGuiKey.UpArrow,
                Key.Down => ImGuiKey.DownArrow,
                Key.PageUp => ImGuiKey.PageUp,
                Key.PageDown => ImGuiKey.PageDown,
                Key.A => ImGuiKey.A,
                Key.C => ImGuiKey.C,
                Key.V => ImGuiKey.V,
                Key.X => ImGuiKey.X,
                Key.LeftCtrl or Key.RightCtrl => ImGuiKey.ModCtrl,
                Key.LeftShift or Key.RightShift => ImGuiKey.ModShift,
                _ => ImGuiKey.None,
            };
        }
    }
}
