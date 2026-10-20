using Magic.Contexts.Input;
using Magic.Interfaces;

namespace Magic.DebugTools;

/// <summary>
/// What every debug window does alike: its key opens and closes it, it has a close button, and its menu has Copy, which
/// puts what it shows on the clipboard. Each window holds one and begins it every frame; while it is open it is begun,
/// and <see cref="End"/> is owed.
/// </summary>
internal sealed class ToolWindow(string name, Control key, IDebugUI ui, IInput? input, IWindow? window)
{
    private readonly string _title = $"{name} ({(key == Control.KeyGrave ? "`" : key.ToString()["Key".Length..])} to toggle)";

    /// <summary>
    /// Toggled by the key, and cleared by the close button while the window is begun.
    /// </summary>
    private bool _isOpen;

    /// <summary>
    /// Opens or closes the window on its key, then, while it is open, begins it with its close button and its menu.
    /// True while it is open: draw what is in it, then End; <paramref name="copy"/> whether Copy was clicked in its
    /// menu, so the window puts what it shows on the clipboard.
    /// </summary>
    public bool Begin(bool scrollable, out bool copy)
    {
        if (input?[key].WasPressed == true)
        {
            _isOpen = !_isOpen;
        }

        copy = false;
        if (!_isOpen)
        {
            return false;
        }

        ui.Begin(_title, ref _isOpen, scrollable);
        copy = ui.MenuItem("Copy");
        return true;
    }

    /// <summary>
    /// Ends what Begin began. Closed by its button, the window stays closed from the next frame.
    /// </summary>
    public void End()
    {
        ui.End();
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard; without a window there is none, and it goes nowhere.
    /// </summary>
    public void Copy(string text)
    {
        if (window is { } shown)
        {
            shown.Clipboard = text;
        }
    }
}
