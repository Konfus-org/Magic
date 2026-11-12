using Magic.Assets;

namespace Magic.Platform;

/// <summary>
/// The window that the game draws into, which a platform gem opens.
/// </summary>
/// <remarks>
/// Use the title and the icon only on the game thread. The OS gets a new title or icon when the gem next updates the
/// window. The platform thread writes the size as one value, and any thread can read it.
/// </remarks>
public interface IWindow
{
    string Title { get; set; }

    /// <summary>
    /// The icon in the title bar and the task bar of the window.
    /// </summary>
    /// <remarks>
    /// It is the default icon until you set it. A new icon shows after it loads.
    /// </remarks>
    Handle<Icon> Icon { get; set; }

    /// <summary>
    /// The width of the area drawn into, in pixels.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// The height of the area drawn into, in pixels.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// The number of window pixels for each point of a UI at the size the OS asks for, for example 1.5 at 150 %.
    /// </summary>
    float DisplayScale { get; }

    /// <summary>
    /// The OS handle of the window, with <see cref="WindowSystem.None"/> if there is no window.
    /// </summary>
    WindowHandle Handle { get; }
}
