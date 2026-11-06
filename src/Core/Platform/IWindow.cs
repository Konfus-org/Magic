using Magic.Files;
using Magic.Files.Types;

namespace Magic.Platform;

/// <summary>
/// The window the game draws into, which a platform gem opens. Its title and icon are the main thread's, and a new title
/// or icon reaches the OS once the gem gets to it. Its size is written whole on the main thread and read from any.
/// </summary>
public interface IWindow
{
    string Title { get; set; }

    /// <summary>
    /// What the window shows in its title bar and task bar: the default icon until it is set. A new one shows once it has
    /// loaded.
    /// </summary>
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
    /// What the OS knows the window by; none when there is no window.
    /// </summary>
    WindowHandle Handle { get; }
}
